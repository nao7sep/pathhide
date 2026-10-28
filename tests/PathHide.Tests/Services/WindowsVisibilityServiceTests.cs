using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PathHide.Models;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

/// <summary>
/// Mirror of the macOS roundtrip for the Windows HIDDEN/SYSTEM attribute path.
/// Written here so the behaviour is covered when the suite runs on Windows/CI;
/// gated to skip on other platforms (setting FileAttributes.Hidden is a no-op on
/// Unix, so the assertions would not be meaningful there).
/// </summary>
public sealed class WindowsVisibilityServiceTests : IDisposable
{
    private readonly string _dir;

    public WindowsVisibilityServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pathhide-win-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private static WindowsVisibilityService Service() => new();

    // How one attribute read is classified runs anywhere; only reading the attributes needs Windows.
    [Theory]
    [InlineData(FileAttributes.Normal, ActualState.Visible, ItemKind.File)]
    [InlineData(FileAttributes.Hidden | FileAttributes.Archive, ActualState.Hidden, ItemKind.File)]
    [InlineData(FileAttributes.System, ActualState.Visible, ItemKind.File)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System, ActualState.Hidden, ItemKind.File)]
    [InlineData(FileAttributes.Directory, ActualState.Visible, ItemKind.Directory)]
    [InlineData(FileAttributes.Directory | FileAttributes.Hidden, ActualState.Hidden, ItemKind.Directory)]
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint, ActualState.Visible, ItemKind.Symlink)]
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Hidden, ActualState.Hidden, ItemKind.Symlink)]
    public void Attributes_classify_into_visibility_and_kind(FileAttributes attributes, ActualState state, ItemKind kind) =>
        Assert.Equal(new PathInspection(state, kind), WindowsVisibilityService.Classify(attributes));

    private string NewFile()
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "x");
        return path;
    }

    private sealed class BrokenReportStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("required report failed");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("required report failed");
    }

    [WindowsOnlyFact]
    public void A_broken_required_report_stops_admission_and_never_claims_the_batch_succeeded()
    {
        var paths = System.Linq.Enumerable.Range(0, 3).Select(_ => NewFile()).ToArray();
        var results = new ElevatedResultsWriter(new StreamWriter(new BrokenReportStream()));
        Assert.Throws<IOException>(() => PathHide.Program.ApplyFileAttributes(paths, true, SystemBit.Keep, results, null));
        Assert.Equal(1, paths.Count(path => File.GetAttributes(path).HasFlag(FileAttributes.Hidden)));
        try { results.Dispose(); } catch (IOException) { }
    }

    [WindowsOnlyFact]
    public void Approval_for_a_replaced_object_cannot_change_its_replacement()
    {
        var path = NewFile();
        using var approved = new WindowsAttributeTarget(path, writable: false);
        var original = path + ".original";
        File.Move(path, original);
        File.WriteAllText(path, "replacement");
        Assert.Throws<IOException>(() => WindowsFileVisibility.Set(path, true, SystemBit.Set, approved.Identity));
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        Assert.False(File.GetAttributes(original).HasFlag(FileAttributes.Hidden));
    }

    [WindowsOnlyFact]
    public void A_pinned_handle_changes_its_object_after_its_path_is_renamed()
    {
        var path = NewFile();
        using var target = new WindowsAttributeTarget(path, writable: true);
        var renamed = path + ".renamed";
        File.Move(path, renamed);
        File.WriteAllText(path, "replacement");
        using var alias = new WindowsAttributeTarget(renamed, writable: false);
        Assert.Equal(target.Identity, alias.Identity);
        Assert.True(target.Set(true, SystemBit.Set, target.Identity));
        Assert.True(File.GetAttributes(renamed).HasFlag(FileAttributes.Hidden));
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
    }

    [WindowsOnlyFact]
    public void Hide_HiddenOnly_SetsHiddenNotSystem()
    {
        var path = NewFile();

        Assert.False(Service().Hide(path, SystemBit.Keep));

        var attrs = File.GetAttributes(path);
        Assert.True(attrs.HasFlag(FileAttributes.Hidden));
        Assert.False(attrs.HasFlag(FileAttributes.System));
        Assert.Equal(ActualState.Hidden, Service().Inspect(path).ActualState);
    }

    [WindowsOnlyFact]
    public void Hide_HiddenAndSystem_SetsBoth_AndReportsTheSystemBitAsSetByPathHide()
    {
        var path = NewFile();

        Assert.True(Service().Hide(path, SystemBit.Set));

        var attrs = File.GetAttributes(path);
        Assert.True(attrs.HasFlag(FileAttributes.Hidden));
        Assert.True(attrs.HasFlag(FileAttributes.System));
    }

    [WindowsOnlyFact]
    public void Show_ClearsAnOwnedSystemBit_AndKeepsOneTheItemAlreadyHad()
    {
        var owned = NewFile();
        Service().Hide(owned, SystemBit.Set);
        var unowned = NewFile();
        File.SetAttributes(unowned, FileAttributes.System);
        Assert.False(Service().Hide(unowned, SystemBit.Set));

        Service().Show(owned, SystemBit.Clear);
        Service().Show(unowned, SystemBit.Keep);

        Assert.False(File.GetAttributes(owned).HasFlag(FileAttributes.Hidden));
        Assert.False(File.GetAttributes(owned).HasFlag(FileAttributes.System));
        Assert.False(File.GetAttributes(unowned).HasFlag(FileAttributes.Hidden));
        Assert.True(File.GetAttributes(unowned).HasFlag(FileAttributes.System));
        Assert.Equal(ActualState.Visible, Service().Inspect(owned).ActualState);
    }

    [WindowsOnlyFact]
    public void Inspect_MissingPathUnderExistingParent_ReturnsMissing()
    {
        var missing = Path.Combine(_dir, "nope.txt");

        Assert.Equal(ActualState.Missing, Service().Inspect(missing).ActualState);
    }

    [WindowsOnlyFact]
    public void Inspect_PathUnderNonexistentParent_ReturnsMissing()
    {
        var missing = Path.Combine(_dir, "no-such-dir", "child");

        // Elevation cannot conjure a missing parent, so this must classify as Missing,
        // never AccessDenied (which would route it into a futile elevated retry).
        Assert.Equal(ActualState.Missing, Service().Inspect(missing).ActualState);
    }

    // Both ordinary and elevated writes preserve the final link itself through the no-follow handle.
    [WindowsOnlyFact]
    public void Replacing_an_approved_ancestor_with_a_junction_cannot_redirect_the_write()
    {
        var original = NewSubdir("original");
        var replacement = NewSubdir("replacement");
        var leaf = Path.Combine(original, "leaf");
        var other = Path.Combine(replacement, "leaf");
        File.WriteAllText(leaf, "original");
        File.WriteAllText(other, "other");
        using var approval = new WindowsAttributeTarget(leaf, writable: false);
        Directory.Move(original, original + ".moved");
        CreateJunction(original, replacement);
        try
        {
            Assert.Throws<IOException>(() => WindowsFileVisibility.Set(leaf, true, SystemBit.Set, approval.Identity));
            Assert.False(File.GetAttributes(other).HasFlag(FileAttributes.Hidden));
            using var alias = new WindowsAttributeTarget(leaf, writable: false);
            using var direct = new WindowsAttributeTarget(other, writable: false);
            Assert.Equal(alias.Identity, direct.Identity);
        }
        finally { Directory.Delete(original); }
    }

    [WindowsOnlyFact]
    public void Hide_Junction_AffectsLinkNotTarget()
    {
        // A junction is creatable without the symbolic-link privilege, so this guard always
        // runs on Windows.
        var target = NewSubdir("junction-target");
        var link = Path.Combine(_dir, "junction-link");
        CreateJunction(link, target);

        Service().Hide(link, SystemBit.Keep);

        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.Hidden));
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
        Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.Hidden));
    }

    [WindowsOnlyFact]
    public void Hide_Symlink_AffectsLinkNotTarget()
    {
        var target = NewFile();
        var link = Path.Combine(_dir, "symlink.txt");

        // Qualification must exercise the link; missing privileges must not look like a pass.
        // Enable Windows Developer Mode before running the ordinary suite.
        File.CreateSymbolicLink(link, target);

        Service().Hide(link, SystemBit.Keep);

        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.Hidden));
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
        Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.Hidden));
    }

    private string NewSubdir(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"mklink /J failed: {process.StandardError.ReadToEnd()}");
    }

}
