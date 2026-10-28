using System.IO;
using PathHide.Models;

namespace PathHide.Services;

/// <summary>
/// What a desired visibility means in file-attribute bits, and the one write of them: the Hidden bit
/// set or cleared per <c>hide</c>, the System bit set, cleared or kept per <c>system</c>, every other
/// attribute left untouched, and nothing written when the item already has those bits. PathHide clears
/// only a System bit it set itself (<see cref="SystemChange"/>), so one an item already had survives
/// Show and Hidden-only hiding.
///
/// <para>This is the ONE place that rule is written. Both writers call <see cref="Set"/> — the
/// ordinary in-process write (<see cref="WindowsVisibilityService"/>) and the elevated <c>apply</c>
/// child — because they must agree: a path hidden normally and the same path hidden through the UAC
/// retry have to end up with the same attributes. Separate copies could diverge near-invisibly, since
/// only this one has tests that run off Windows.</para>
///
/// <para><see cref="Change"/> is pure, so the bit math and the skip are unit-tested without touching
/// a real file or running on Windows — the <see cref="FileAttributes"/> flags exist on every
/// platform, only their on-disk effect is Windows-specific.</para>
/// </summary>
public static class WindowsFileVisibility
{
    /// <summary>
    /// The attributes to write for the desired visibility, or null when <paramref name="current"/>
    /// already has them (content-lifecycle conventions: a write that changes nothing is skipped).
    /// </summary>
    public static FileAttributes? Change(FileAttributes current, bool hide, SystemBit system)
    {
        var updated = current;
        if (hide)
            updated |= FileAttributes.Hidden;
        else
            updated &= ~FileAttributes.Hidden;

        if (system == SystemBit.Set)
            updated |= FileAttributes.System;
        else if (system == SystemBit.Clear)
            updated &= ~FileAttributes.System;

        return updated == current ? null : updated;
    }

    /// <summary>
    /// What a write does to System: Hidden + System hiding sets it; otherwise it is cleared only when
    /// PathHide set it (<paramref name="owned"/>), and kept as the item has it when not.
    /// </summary>
    public static SystemBit SystemChange(bool hide, WindowsHideMode mode, bool owned) =>
        hide && mode == WindowsHideMode.HiddenAndSystem ? SystemBit.Set
        : owned ? SystemBit.Clear
        : SystemBit.Keep;

    /// <summary>
    /// Whether PathHide owns System after a write that landed: one it set itself (<paramref name="setNow"/>)
    /// or already owned and kept setting, and none once it cleared it.
    /// </summary>
    public static bool OwnsSystemAfter(SystemBit change, bool owned, bool setNow) => change switch
    {
        SystemBit.Set => owned || setNow,
        SystemBit.Clear => false,
        _ => owned,
    };

    /// <summary>Whether PathHide owns the entry's System bit; absent evidence never grants permission to clear it.</summary>
    public static bool OwnsSystem(PathEntry entry) => entry.SystemSetByPathHide == true;

    /// <summary>
    /// Sets <paramref name="path"/>'s Hidden and System bits, writing only when they change. Returns
    /// whether this write turned System on, which makes it PathHide's.
    /// </summary>
    public static bool Set(string path, bool hide, SystemBit system, string? approvedIdentity = null)
    {
        using var target = new WindowsAttributeTarget(path, writable: true);
        return target.Set(hide, system, approvedIdentity);
    }
}
