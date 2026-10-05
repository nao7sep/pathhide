using System.IO;

namespace PathHide.Services;

/// <summary>
/// What a desired visibility means in file-attribute bits, and the one write of them: the Hidden bit
/// set or cleared per <c>hide</c>, the System bit set or cleared per <c>system</c>, every other
/// attribute left untouched, and nothing written when the item already has those bits.
///
/// <para>This is the ONE place that rule is written. Both writers call <see cref="Set"/> — the
/// ordinary in-process write (<see cref="WindowsVisibilityService"/>) and the elevated <c>apply</c>
/// child — because they must agree: a path hidden normally and the same path hidden through the UAC
/// retry have to end up with the same attributes. They were separate copies, and the divergence
/// would have been near-invisible, since only the extracted copy has tests that run off
/// Windows.</para>
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
    public static FileAttributes? Change(FileAttributes current, bool hide, bool system)
    {
        var updated = current;
        if (hide)
            updated |= FileAttributes.Hidden;
        else
            updated &= ~FileAttributes.Hidden;

        if (system)
            updated |= FileAttributes.System;
        else
            updated &= ~FileAttributes.System;

        return updated == current ? null : updated;
    }

    /// <summary>
    /// Sets <paramref name="path"/>'s Hidden and System bits, writing only when they change.
    /// </summary>
    /// <remarks>
    /// <c>File.GetAttributes</c>/<c>File.SetAttributes</c> operate on the reparse point itself, not
    /// its target (verified on Windows for symlinks and junctions, elevated and not). So a path
    /// swapped for a junction between the unelevated inspect and the elevated write can only have its
    /// own attributes changed — it cannot redirect an admin write onto the link's target. Keep both
    /// calls path-based for that reason; do not switch to a follow-based API or add reparse-handle
    /// machinery to "harden" a hazard that cannot occur.
    /// </remarks>
    public static void Set(string path, bool hide, bool system)
    {
        if (Change(File.GetAttributes(path), hide, system) is not { } updated)
            return;

        // not recorded: this changes only external filesystem metadata; paths.json
        // records the user's desired visibility and tracked-path identity.
        File.SetAttributes(path, updated);
    }
}
