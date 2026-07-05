using System;
using System.Diagnostics;
using System.IO;
using PathHide.Models;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

/// <summary>
/// Integration tests for the macOS hidden-flag path, run against real temp files
/// on a real mac. This is the guard for the <c>getattrlist</c>/<c>chflags</c>
/// P/Invoke in <c>MacFs</c> — a regression in the attribute-bitmap
/// constant (a documented past bug) makes the read path disagree with the write
/// path, which the hide/inspect roundtrips below would catch. Each flag assertion
/// is cross-checked with <c>stat(1)</c> so it does not rely solely on the code
/// under test.
/// </summary>
public sealed class MacVisibilityServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly MacVisibilityService _service = new();

    public MacVisibilityServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pathhide-mac-tests", NanoId.New());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private string NewFile(string name = "file.txt")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>Symbolic BSD flags via <c>stat -f %Sf</c> (does not follow symlinks).</summary>
    private static string StatFlags(string path) => RunStat(["-f", "%Sf", path]);

    private static string RunStat(string[] args) => Run("/usr/bin/stat", args);

    private static void Chflags(string spec, string path) => Run("/usr/bin/chflags", [spec, path]);

    private static string Run(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout.Trim();
    }

    [MacOnlyFact]
    public void Inspect_NewFile_IsVisibleFile()
    {
        var path = NewFile();

        var result = _service.Inspect(path);

        Assert.Equal(ActualState.Visible, result.ActualState);
        Assert.Equal(ItemKind.File, result.ItemKind);
    }

    [MacOnlyFact]
    public void Hide_SetsHiddenFlag_AndInspectReportsHidden()
    {
        var path = NewFile();

        _service.Hide(path);

        Assert.Contains("hidden", StatFlags(path));                 // independent OS-level check
        Assert.Equal(ActualState.Hidden, _service.Inspect(path).ActualState);
    }

    [MacOnlyFact]
    public void Show_ClearsHiddenFlag()
    {
        var path = NewFile();
        _service.Hide(path);

        _service.Show(path);

        Assert.DoesNotContain("hidden", StatFlags(path));
        Assert.Equal(ActualState.Visible, _service.Inspect(path).ActualState);
    }

    [MacOnlyFact]
    public void Hide_IsIdempotent()
    {
        var path = NewFile();

        _service.Hide(path);
        _service.Hide(path); // must not throw; no-op when already hidden

        Assert.Equal(ActualState.Hidden, _service.Inspect(path).ActualState);
    }

    [MacOnlyFact]
    public void Show_OnAlreadyVisibleFile_IsNoOp()
    {
        var path = NewFile();

        _service.Show(path); // already visible

        Assert.Equal(ActualState.Visible, _service.Inspect(path).ActualState);
    }

    [MacOnlyFact]
    public void Hide_PreservesUnrelatedFlags_AndShowLeavesThemIntact()
    {
        var path = NewFile();
        Chflags("nodump", path);

        _service.Hide(path);
        var afterHide = StatFlags(path);
        Assert.Contains("hidden", afterHide);
        Assert.Contains("nodump", afterHide);   // unrelated flag must survive Hide

        _service.Show(path);
        var afterShow = StatFlags(path);
        Assert.DoesNotContain("hidden", afterShow);
        Assert.Contains("nodump", afterShow);    // and survive Show
    }

    [MacOnlyFact]
    public void Inspect_Directory_ReportsDirectoryKind()
    {
        var sub = Path.Combine(_dir, "subdir");
        Directory.CreateDirectory(sub);

        var result = _service.Inspect(sub);

        Assert.Equal(ItemKind.Directory, result.ItemKind);
        Assert.Equal(ActualState.Visible, result.ActualState);
    }

    [MacOnlyFact]
    public void Inspect_MissingPathUnderExistingParent_ReturnsMissing()
    {
        var missing = Path.Combine(_dir, "does-not-exist");

        var result = _service.Inspect(missing);

        Assert.Equal(ActualState.Missing, result.ActualState);
    }

    [MacOnlyFact]
    public void Inspect_PathUnderNonexistentParent_ReturnsMissing()
    {
        var missing = Path.Combine(_dir, "no-such-dir", "child");

        var result = _service.Inspect(missing);

        // A path whose parent does not exist is genuinely missing, not a permission wall —
        // it must not be reported as access-denied (which would route it into a futile
        // elevated retry on Windows).
        Assert.Equal(ActualState.Missing, result.ActualState);
    }

    [MacOnlyFact]
    public void Inspect_PathUnderUnreadableParent_ReturnsAccessDenied()
    {
        var locked = Path.Combine(_dir, "locked");
        Directory.CreateDirectory(locked);
        var child = Path.Combine(locked, "child.txt");
        File.WriteAllText(child, "x");

        // Strip all permissions from the parent so its owner can no longer search into it;
        // statting the child then hits a permission wall (EACCES), not a not-found. This is
        // the AccessDenied state the Windows elevated retry exists to recover.
        Run("/bin/chmod", ["000", locked]);
        try
        {
            Assert.Equal(ActualState.AccessDenied, _service.Inspect(child).ActualState);
        }
        finally
        {
            // Restore access so the fixture can be torn down.
            Run("/bin/chmod", ["755", locked]);
        }
    }

    [MacOnlyFact]
    public void Hide_Symlink_AffectsLinkNotTarget()
    {
        var target = NewFile("target.txt");
        var link = Path.Combine(_dir, "link.txt");
        File.CreateSymbolicLink(link, target);

        _service.Hide(link);

        // The link itself is hidden and reported as a symlink...
        var linkInspection = _service.Inspect(link);
        Assert.Equal(ActualState.Hidden, linkInspection.ActualState);
        Assert.Equal(ItemKind.Symlink, linkInspection.ItemKind);

        // ...while the target it points to is left untouched.
        Assert.Equal(ActualState.Visible, _service.Inspect(target).ActualState);
    }
}
