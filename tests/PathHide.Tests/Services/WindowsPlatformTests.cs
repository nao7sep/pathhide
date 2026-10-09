using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using PathHide.Models;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

/// <summary>
/// Windows behavior that only the real platform shows: the elevated <c>apply</c> child run as an ordinary
/// process, an ACL that denies writing attributes, and the single-instance claim on a case-insensitive
/// file system. Skipped elsewhere; the developer's Windows run executes them.
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

        // The child is the app itself in apply mode, started the way the runas launch starts it but
        // without the elevation, so it runs here without a consent prompt.
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(App).Assembly.Location);
        foreach (var argument in ElevatedApplyCommand.BuildArguments(request, results, Path.Combine(_dir, "root")))
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;

        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(60)), "The apply child did not exit.");
        Assert.Equal(0, child.ExitCode);
        Assert.Equal(FileAttributes.Hidden, File.GetAttributes(hide) & (FileAttributes.Hidden | FileAttributes.System));
        Assert.Equal(FileAttributes.Hidden | FileAttributes.System,
            File.GetAttributes(system) & (FileAttributes.Hidden | FileAttributes.System));
        Assert.Equal((FileAttributes)0, File.GetAttributes(show) & (FileAttributes.Hidden | FileAttributes.System));

        var report = ElevatedApplyResults.Parse(File.ReadAllText(results));
        Assert.Equal([hide, system, show], report.Results.Select(result => result.Path));
        Assert.All(report.Results, result => Assert.True(result.Ok));
    }

    [WindowsOnlyFact]
    public void Hiding_a_file_whose_ACL_denies_writing_attributes_raises_access_denied_for_the_elevated_retry()
    {
        var path = NewFile("denied.txt");
        DenyWriteAttributes(path);

        Assert.Throws<UnauthorizedAccessException>(() =>
            new WindowsVisibilityService(() => WindowsHideMode.HiddenOnly).Hide(path));
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

        new WindowsVisibilityService(() => WindowsHideMode.HiddenOnly).Hide(hidden);
        WindowsFileVisibility.Set(hidden, hide: true, system: false);
        new WindowsVisibilityService(() => WindowsHideMode.HiddenAndSystem).Hide(hiddenAndSystem);
        WindowsFileVisibility.Set(hiddenAndSystem, hide: true, system: true);
    }

    [WindowsOnlyFact]
    public void Data_roots_that_differ_only_in_letter_case_are_one_instance()
    {
        var root = Path.Combine(_dir, "Root");
        Directory.CreateDirectory(root);
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
}
