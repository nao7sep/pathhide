using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Infrastructure;
using Xunit;

namespace PathHide.Tests.Services;

/// <summary>
/// Windows behavior that only the real platform shows: the elevated <c>apply</c> child run as an ordinary
/// process, an ACL that denies writing attributes, and the single-instance claim on a case-insensitive
/// file system. Skipped elsewhere; the ordinary Windows run executes them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pathhide-win-platform-").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir))
        {
            try
            {
                AllowWriteAttributes(file);
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch
            {
                // best-effort, so the delete below can still try
            }
        }
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private string NewFile(string name, FileAttributes attributes = FileAttributes.Normal)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        File.SetAttributes(path, attributes);
        return path;
    }

    private static FileSystemAccessRule DenyWriteAttributes() =>
        new(WindowsIdentity.GetCurrent().User!, FileSystemRights.WriteAttributes, AccessControlType.Deny);

    private static void DenyWriteAttributes(string path)
    {
        var file = new FileInfo(path);
        var acl = file.GetAccessControl();
        acl.AddAccessRule(DenyWriteAttributes());
        file.SetAccessControl(acl);
    }

    private static void AllowWriteAttributes(string path)
    {
        var file = new FileInfo(path);
        var acl = file.GetAccessControl();
        acl.RemoveAccessRule(DenyWriteAttributes());
        file.SetAccessControl(acl);
    }

    [WindowsOnlyFact]
    public void The_apply_child_run_unelevated_sets_each_attribute_reports_each_path_and_exits_zero()
    {
        var hide = NewFile("hide.txt");
        var system = NewFile("system.txt");
        var show = NewFile("show.txt", FileAttributes.Hidden | FileAttributes.System);
        var request = Path.Combine(_dir, "request.json");
        var results = Path.Combine(_dir, "results.jsonl");
        File.WriteAllText(request, ElevatedApplyCommand.SerializeRequest(
            new ElevatedApplyCommand.Buckets([hide], [system], [show])));

        Assert.Equal(0, ApplyChildProcess.Run(request, results, Path.Combine(_dir, "root")));
        Assert.Equal(FileAttributes.Hidden, File.GetAttributes(hide) & (FileAttributes.Hidden | FileAttributes.System));
        Assert.Equal(FileAttributes.Hidden | FileAttributes.System,
            File.GetAttributes(system) & (FileAttributes.Hidden | FileAttributes.System));
        Assert.Equal((FileAttributes)0, File.GetAttributes(show) & (FileAttributes.Hidden | FileAttributes.System));

        var report = ElevatedApplyResults.Parse(File.ReadAllText(results));
        Assert.Equal([hide, system, show], report.Results.Select(result => result.Path));
        Assert.All(report.Results, result => Assert.True(result.Ok));
    }

    [WindowsOnlyFact]
    public void A_failed_path_does_not_stop_later_paths_or_lose_their_reports()
    {
        var denied = NewFile("denied-first.txt");
        DenyWriteAttributes(denied);
        var hidden = NewFile("hidden-later.txt");
        var shown = NewFile("shown-later.txt", FileAttributes.Hidden | FileAttributes.System);
        var request = Path.Combine(_dir, "request.json");
        var results = Path.Combine(_dir, "results.jsonl");
        File.WriteAllText(request, ElevatedApplyCommand.SerializeRequest(new([denied, hidden], [], [shown])));

        Assert.Equal(1, ApplyChildProcess.Run(request, results, Path.Combine(_dir, "root")));

        var report = ElevatedApplyResults.Parse(File.ReadAllText(results));
        Assert.Equal([new PathApplyResult(denied, false), new PathApplyResult(hidden, true),
            new PathApplyResult(shown, true)], report.Results);
        Assert.Equal((FileAttributes)0, File.GetAttributes(denied) & FileAttributes.Hidden);
        Assert.Equal(FileAttributes.Hidden, File.GetAttributes(hidden) & FileAttributes.Hidden);
        Assert.Equal((FileAttributes)0, File.GetAttributes(shown) & (FileAttributes.Hidden | FileAttributes.System));
    }

    [WindowsOnlyFact]
    public void Hiding_a_file_whose_ACL_denies_writing_attributes_raises_access_denied_for_the_elevated_retry()
    {
        var path = NewFile("denied.txt");
        DenyWriteAttributes(path);

        Assert.Throws<UnauthorizedAccessException>(() =>
            new WindowsVisibilityService().Hide(path, WindowsHideMode.HiddenOnly));
    }

    [WindowsOnlyFact]
    public void A_file_already_in_the_wanted_state_is_not_written_on_either_path()
    {
        // With writing attributes denied, any write would throw: succeeding proves neither the normal
        // path nor the elevated child's writer writes an unchanged state.
        var hidden = NewFile("hidden.txt", FileAttributes.Hidden);
        var hiddenAndSystem = NewFile("hidden-system.txt", FileAttributes.Hidden | FileAttributes.System);
        DenyWriteAttributes(hidden);
        DenyWriteAttributes(hiddenAndSystem);

        new WindowsVisibilityService().Hide(hidden, WindowsHideMode.HiddenOnly);
        WindowsFileVisibility.Set(hidden, hide: true, system: false);
        new WindowsVisibilityService().Hide(hiddenAndSystem, WindowsHideMode.HiddenAndSystem);
        WindowsFileVisibility.Set(hiddenAndSystem, hide: true, system: true);
    }

    [WindowsOnlyFact]
    public void Data_roots_that_differ_only_in_letter_case_are_one_instance()
    {
        var root = Path.Combine(_dir, "Root");
        Directory.CreateDirectory(root);
        Assert.StartsWith(@"Global\", SingleInstanceLease.MutexName(root));
        Assert.Equal(SingleInstanceLease.MutexName(root.ToLowerInvariant()),
            SingleInstanceLease.MutexName(root.ToUpperInvariant()));

        Assert.True(SingleInstanceLease.TryAcquire(root.ToLowerInvariant(), out var owner));
        using (owner)
        {
            SingleInstanceLease.RegisterOwnerActivationHandler(() => { });
            Assert.False(SingleInstanceLease.TryAcquire(root.ToUpperInvariant(), out var duplicate));
            Assert.Null(duplicate);
        }
    }

    private static void Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true,
            RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Fixture command did not finish.");
        }
        Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
    }

    private static void AssertOneWriter(string root, string alias)
    {
        Assert.Equal(SingleInstanceLease.MutexName(root), SingleInstanceLease.MutexName(alias));
        Assert.True(SingleInstanceLease.TryAcquire(root, out var owner));
        using (owner)
        {
            SingleInstanceLease.RegisterOwnerActivationHandler(() => { });
            Assert.False(SingleInstanceLease.TryAcquire(alias, out var duplicate));
            Assert.Null(duplicate);
        }
    }

    [WindowsOnlyFact]
    public void A_parent_junction_is_the_same_managed_data_root()
    {
        var root = Directory.CreateDirectory(Path.Combine(_dir, "Root")).FullName;
        var junction = Path.Combine(_dir, "alias");
        Run("cmd.exe", "/c", "mklink", "/J", junction, _dir);
        try { AssertOneWriter(root, Path.Combine(junction, "Root")); }
        finally { Directory.Delete(junction); }
    }

    [WindowsOnlyFact]
    public void A_subst_drive_is_the_same_managed_data_root()
    {
        var occupied = Directory.GetLogicalDrives().Select(path => char.ToUpperInvariant(path[0])).ToHashSet();
        var letter = Enumerable.Range('D', 'Z' - 'D' + 1).Reverse().Select(value => (char)value)
            .First(value => !occupied.Contains(value));
        var drive = letter + ":";
        var root = Directory.CreateDirectory(Path.Combine(_dir, "Root")).FullName;
        Run("subst.exe", drive, _dir);
        try { AssertOneWriter(root, Path.Combine(drive + @"\", "Root")); }
        finally { Run("subst.exe", drive, "/d"); }
    }

    [WindowsOnlyFact]
    public void Long_data_root_identity_grows_the_native_output_buffer()
    {
        var root = _dir;
        for (var index = 0; index < 7; index++)
            root = Path.Combine(root, new string('a', 90));
        Directory.CreateDirectory(root);
        AssertOneWriter(root, root + Path.DirectorySeparatorChar);
    }

    [WindowsOnlyFact]
    public void A_foreign_posix_spelling_cannot_change_a_colliding_drive_rooted_file()
    {
        var previous = Environment.CurrentDirectory;
        var actual = NewFile("foreign.txt");
        try
        {
            Environment.CurrentDirectory = _dir;
            var foreign = actual[2..].Replace('\\', '/');
            var service = new WindowsVisibilityService();
            Assert.Equal(ActualState.Error, service.Inspect(foreign).ActualState);
            Assert.Throws<ArgumentException>(() => service.Hide(foreign, WindowsHideMode.HiddenOnly));
            Assert.Throws<ArgumentException>(() => service.Show(foreign));
            Assert.Throws<ArgumentException>(() => WindowsFileVisibility.Set(foreign, true, true));
            Assert.Equal((FileAttributes)0, File.GetAttributes(actual) & (FileAttributes.Hidden | FileAttributes.System));
        }
        finally { Environment.CurrentDirectory = previous; }
    }
}
