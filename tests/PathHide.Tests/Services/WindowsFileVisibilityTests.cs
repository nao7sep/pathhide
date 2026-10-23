using System.IO;
using PathHide.Models;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

/// <summary>
/// The Hidden/System bit math both Windows writers apply per desired visibility, the skip when an
/// item already has those bits, and which System bit is PathHide's to clear. Pure, so it is pinned
/// here cross-platform — the on-disk effect is Windows-only, but the <see cref="FileAttributes"/> flag
/// arithmetic is not.
/// </summary>
public sealed class WindowsFileVisibilityTests
{
    [Fact]
    public void Change_Hide_SetsHidden_AndShowClearsItAgain()
    {
        var hidden = WindowsFileVisibility.Change(FileAttributes.Normal, hide: true, SystemBit.Keep);
        Assert.NotNull(hidden);
        Assert.True(hidden.Value.HasFlag(FileAttributes.Hidden));
        Assert.False(hidden.Value.HasFlag(FileAttributes.System));

        var shown = WindowsFileVisibility.Change(hidden.Value, hide: false, SystemBit.Keep);
        Assert.NotNull(shown);
        Assert.False(shown.Value.HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void Change_HideWithSystem_SetsBothBits()
    {
        var result = WindowsFileVisibility.Change(FileAttributes.Normal, hide: true, SystemBit.Set);
        Assert.NotNull(result);
        Assert.True(result.Value.HasFlag(FileAttributes.Hidden));
        Assert.True(result.Value.HasFlag(FileAttributes.System));
    }

    // A System bit the item had before PathHide survives Show and Hidden-only hiding; one PathHide
    // set is cleared again.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Change_KeepsAnUnownedSystemBit_AndClearsAnOwnedOne(bool hide)
    {
        var withSystem = FileAttributes.System | (hide ? FileAttributes.Normal : FileAttributes.Hidden);

        var kept = WindowsFileVisibility.Change(withSystem, hide, SystemBit.Keep);
        var cleared = WindowsFileVisibility.Change(withSystem, hide, SystemBit.Clear);

        Assert.True((kept ?? withSystem).HasFlag(FileAttributes.System));
        Assert.NotNull(cleared);
        Assert.False(cleared.Value.HasFlag(FileAttributes.System));
        Assert.Equal(hide, cleared.Value.HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void Change_PreservesUnrelatedAttributes()
    {
        // Flipping Hidden/System must not disturb an unrelated bit such as ReadOnly.
        var result = WindowsFileVisibility.Change(FileAttributes.ReadOnly, hide: true, SystemBit.Clear);
        Assert.NotNull(result);
        Assert.True(result.Value.HasFlag(FileAttributes.ReadOnly));
        Assert.True(result.Value.HasFlag(FileAttributes.Hidden));
    }

    [Theory]
    [InlineData(FileAttributes.Hidden | FileAttributes.Archive, true, SystemBit.Clear)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System, true, SystemBit.Set)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System, true, SystemBit.Keep)]
    [InlineData(FileAttributes.Archive, false, SystemBit.Clear)]
    [InlineData(FileAttributes.System, false, SystemBit.Keep)]
    [InlineData(FileAttributes.Normal, false, SystemBit.Keep)]
    public void Change_WhenTheItemAlreadyHasTheBits_WritesNothing(FileAttributes current, bool hide, SystemBit system)
    {
        // A no-op write would still touch the item's change time and wake every watcher of it.
        Assert.Null(WindowsFileVisibility.Change(current, hide, system));
    }

    // Hidden + System hiding sets System; anything else clears it only where PathHide owns it.
    [Theory]
    [InlineData(true, WindowsHideMode.HiddenAndSystem, false, SystemBit.Set)]
    [InlineData(true, WindowsHideMode.HiddenAndSystem, true, SystemBit.Set)]
    [InlineData(true, WindowsHideMode.HiddenOnly, true, SystemBit.Clear)]
    [InlineData(true, WindowsHideMode.HiddenOnly, false, SystemBit.Keep)]
    [InlineData(false, WindowsHideMode.HiddenAndSystem, true, SystemBit.Clear)]
    [InlineData(false, WindowsHideMode.HiddenAndSystem, false, SystemBit.Keep)]
    public void SystemChange_ClearsOnlyAnOwnedBit(bool hide, WindowsHideMode mode, bool owned, SystemBit expected) =>
        Assert.Equal(expected, WindowsFileVisibility.SystemChange(hide, mode, owned));

    // A bit PathHide turned on becomes its own; one the item already had stays the item's, and a
    // clear gives up ownership.
    [Theory]
    [InlineData(SystemBit.Set, false, true, true)]
    [InlineData(SystemBit.Set, false, false, false)]
    [InlineData(SystemBit.Set, true, false, true)]
    [InlineData(SystemBit.Clear, true, false, false)]
    [InlineData(SystemBit.Keep, false, false, false)]
    public void OwnsSystemAfter_FollowsWhatTheWriteDid(SystemBit change, bool owned, bool setNow, bool expected) =>
        Assert.Equal(expected, WindowsFileVisibility.OwnsSystemAfter(change, owned, setNow));

    // A list from before ownership was recorded keeps the behaviour it had: PathHide's to clear.
    [Theory]
    [InlineData(null, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void An_entry_without_a_record_counts_as_PathHides(bool? recorded, bool expected) =>
        Assert.Equal(expected, WindowsFileVisibility.OwnsSystem(
            new PathEntry { Path = @"C:\a", DesiredVisibility = DesiredVisibility.Hidden, SystemSetByPathHide = recorded }));
}
