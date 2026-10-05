using System.IO;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

/// <summary>
/// The Hidden/System bit math both Windows writers apply per desired visibility, and the skip when an
/// item already has those bits. Pure, so it is pinned here cross-platform — the on-disk effect is
/// Windows-only, but the <see cref="FileAttributes"/> flag arithmetic is not.
/// </summary>
public sealed class WindowsFileVisibilityTests
{
    [Fact]
    public void Change_Hide_SetsHidden_AndShowClearsItAgain()
    {
        var hidden = WindowsFileVisibility.Change(FileAttributes.Normal, hide: true, system: false);
        Assert.NotNull(hidden);
        Assert.True(hidden.Value.HasFlag(FileAttributes.Hidden));
        Assert.False(hidden.Value.HasFlag(FileAttributes.System));

        var shown = WindowsFileVisibility.Change(hidden.Value, hide: false, system: false);
        Assert.NotNull(shown);
        Assert.False(shown.Value.HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void Change_HideWithSystem_SetsBothBits()
    {
        var result = WindowsFileVisibility.Change(FileAttributes.Normal, hide: true, system: true);
        Assert.NotNull(result);
        Assert.True(result.Value.HasFlag(FileAttributes.Hidden));
        Assert.True(result.Value.HasFlag(FileAttributes.System));
    }

    [Fact]
    public void Change_Show_ClearsHiddenAndSystem_RegardlessOfPriorState()
    {
        var both = FileAttributes.Hidden | FileAttributes.System;
        var result = WindowsFileVisibility.Change(both, hide: false, system: false);
        Assert.NotNull(result);
        Assert.False(result.Value.HasFlag(FileAttributes.Hidden));
        Assert.False(result.Value.HasFlag(FileAttributes.System));
    }

    [Fact]
    public void Change_PreservesUnrelatedAttributes()
    {
        // Flipping Hidden/System must not disturb an unrelated bit such as ReadOnly.
        var result = WindowsFileVisibility.Change(FileAttributes.ReadOnly, hide: true, system: false);
        Assert.NotNull(result);
        Assert.True(result.Value.HasFlag(FileAttributes.ReadOnly));
        Assert.True(result.Value.HasFlag(FileAttributes.Hidden));
    }

    [Theory]
    [InlineData(FileAttributes.Hidden | FileAttributes.Archive, true, false)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System, true, true)]
    [InlineData(FileAttributes.Archive, false, false)]
    [InlineData(FileAttributes.Normal, false, false)]
    public void Change_WhenTheItemAlreadyHasTheBits_WritesNothing(FileAttributes current, bool hide, bool system)
    {
        // A no-op write would still touch the item's change time and wake every watcher of it.
        Assert.Null(WindowsFileVisibility.Change(current, hide, system));
    }
}
