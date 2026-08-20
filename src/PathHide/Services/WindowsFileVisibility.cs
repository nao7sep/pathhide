using System.IO;

namespace PathHide.Services;

/// <summary>
/// What a desired visibility means in file-attribute bits: the Hidden bit set or cleared per
/// <c>hide</c>, the System bit set or cleared per <c>system</c>, every other attribute left
/// untouched.
///
/// <para>This is the ONE place that rule is written. Both writers call it — the ordinary
/// in-process write (<see cref="WindowsVisibilityService"/>) and the elevated <c>apply</c> child
/// — because they must agree: a path hidden normally and the same path hidden through the UAC
/// retry have to end up with the same attributes. They were separate copies, and the divergence
/// would have been near-invisible, since only the extracted copy has tests that run off
/// Windows.</para>
///
/// <para>Pure, so the bit math is unit-tested without touching a real file or running on Windows
/// — the <see cref="FileAttributes"/> flags exist on every platform, only their on-disk effect is
/// Windows-specific.</para>
/// </summary>
public static class WindowsFileVisibility
{
    public static FileAttributes ApplyVisibility(FileAttributes current, bool hide, bool system)
    {
        if (hide)
            current |= FileAttributes.Hidden;
        else
            current &= ~FileAttributes.Hidden;

        if (system)
            current |= FileAttributes.System;
        else
            current &= ~FileAttributes.System;

        return current;
    }
}
