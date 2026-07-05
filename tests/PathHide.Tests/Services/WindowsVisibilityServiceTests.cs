using System;
using System.Diagnostics;
using System.IO;
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
    private WindowsHideMode _mode = WindowsHideMode.HiddenOnly;

    public WindowsVisibilityServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pathhide-win-tests", NanoId.New());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private WindowsVisibilityService Service() => new(() => _mode);

    private string NewFile()
    {
        var path = Path.Combine(_dir, "file.txt");
        File.WriteAllText(path, "x");
        return path;
    }

    [WindowsOnlyFact]
    public void Hide_HiddenOnly_SetsHiddenNotSystem()
    {
        var path = NewFile();
        _mode = WindowsHideMode.HiddenOnly;

        Service().Hide(path);

        var attrs = File.GetAttributes(path);
        Assert.True(attrs.HasFlag(FileAttributes.Hidden));
        Assert.False(attrs.HasFlag(FileAttributes.System));
        Assert.Equal(ActualState.Hidden, Service().Inspect(path).ActualState);
    }

    [WindowsOnlyFact]
    public void Hide_HiddenAndSystem_SetsBoth()
    {
        var path = NewFile();
        _mode = WindowsHideMode.HiddenAndSystem;

        Service().Hide(path);

        var attrs = File.GetAttributes(path);
        Assert.True(attrs.HasFlag(FileAttributes.Hidden));
        Assert.True(attrs.HasFlag(FileAttributes.System));
    }

    [WindowsOnlyFact]
    public void Show_ClearsHiddenAndSystem_RegardlessOfMode()
    {
        var path = NewFile();
        _mode = WindowsHideMode.HiddenAndSystem;
        Service().Hide(path);

        Service().Show(path);

        var attrs = File.GetAttributes(path);
        Assert.False(attrs.HasFlag(FileAttributes.Hidden));
        Assert.False(attrs.HasFlag(FileAttributes.System));
        Assert.Equal(ActualState.Visible, Service().Inspect(path).ActualState);
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

    // --- Reparse points must not be followed (security: TOCTOU redirection) ---
    //
    // Hide/Show — and the elevated apply pass, which uses the same File.SetAttributes call —
    // must change the LINK's own attributes, never the target's. Otherwise a path swapped for
    // a junction/symlink pointing into a sensitive location (e.g. C:\Windows) between the
    // unelevated inspect and the elevated write could redirect that admin write onto the
    // target. Windows measurement confirmed both Get/SetAttributes are no-follow; these lock
    // that in so a future switch to a follow-based API is caught.

    [WindowsOnlyFact]
    public void Hide_Junction_AffectsLinkNotTarget()
    {
        // A junction is creatable without the symbolic-link privilege, so this guard always
        // runs on Windows.
        var target = NewSubdir("junction-target");
        var link = Path.Combine(_dir, "junction-link");
        CreateJunction(link, target);

        Service().Hide(link);

        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.Hidden));
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
        Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.Hidden));
    }

    [WindowsOnlyFact]
    public void Hide_Symlink_AffectsLinkNotTarget()
    {
        var target = NewFile();
        var link = Path.Combine(_dir, "symlink.txt");

        // Creating a symbolic link needs SeCreateSymbolicLinkPrivilege (admin or Developer
        // Mode). When it is absent the test cannot run, so it no-ops rather than failing — the
        // junction test above covers the no-follow guarantee in that case, and this adds the
        // symlink variant wherever the privilege is granted.
        if (!TryCreateFileSymlink(link, target))
            return;

        Service().Hide(link);

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

    private static bool TryCreateFileSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Privilege not held (ERROR_PRIVILEGE_NOT_HELD).
            return false;
        }
    }
}
