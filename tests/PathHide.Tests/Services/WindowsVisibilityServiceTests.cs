using System;
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
        _dir = Path.Combine(Path.GetTempPath(), "pathhide-win-tests", Guid.NewGuid().ToString("N"));
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
}
