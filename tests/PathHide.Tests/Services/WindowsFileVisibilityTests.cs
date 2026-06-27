using System.IO;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

/// <summary>
/// The child side of the elevated-apply contract: the Hidden/System bit math the elevated apply pass
/// writes per desired visibility. Pure, so it is pinned here cross-platform — the on-disk effect is
/// Windows-only, but the <see cref="FileAttributes"/> flag arithmetic is not.
/// </summary>
public sealed class WindowsFileVisibilityTests
{
    [Fact]
    public void ApplyVisibility_Hide_SetsHidden_AndShowClearsItAgain()
    {
        var hidden = WindowsFileVisibility.ApplyVisibility(FileAttributes.Normal, hide: true, system: false);
        Assert.True(hidden.HasFlag(FileAttributes.Hidden));
        Assert.False(hidden.HasFlag(FileAttributes.System));

        var shown = WindowsFileVisibility.ApplyVisibility(hidden, hide: false, system: false);
        Assert.False(shown.HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void ApplyVisibility_HideWithSystem_SetsBothBits()
    {
        var result = WindowsFileVisibility.ApplyVisibility(FileAttributes.Normal, hide: true, system: true);
        Assert.True(result.HasFlag(FileAttributes.Hidden));
        Assert.True(result.HasFlag(FileAttributes.System));
    }

    [Fact]
    public void ApplyVisibility_Show_ClearsHiddenAndSystem_RegardlessOfPriorState()
    {
        var both = FileAttributes.Hidden | FileAttributes.System;
        var result = WindowsFileVisibility.ApplyVisibility(both, hide: false, system: false);
        Assert.False(result.HasFlag(FileAttributes.Hidden));
        Assert.False(result.HasFlag(FileAttributes.System));
    }

    [Fact]
    public void ApplyVisibility_PreservesUnrelatedAttributes()
    {
        // Flipping Hidden/System must not disturb an unrelated bit such as ReadOnly.
        var result = WindowsFileVisibility.ApplyVisibility(FileAttributes.ReadOnly, hide: true, system: false);
        Assert.True(result.HasFlag(FileAttributes.ReadOnly));
        Assert.True(result.HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void ApplyVisibility_IsIdempotent()
    {
        var once = WindowsFileVisibility.ApplyVisibility(FileAttributes.Normal, hide: true, system: true);
        var twice = WindowsFileVisibility.ApplyVisibility(once, hide: true, system: true);
        Assert.Equal(once, twice);
    }
}
