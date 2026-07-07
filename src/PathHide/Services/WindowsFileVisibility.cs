using System.IO;

namespace PathHide.Services;

/// <summary>
/// The file-attribute decision the elevated <c>apply</c> child writes for a desired visibility: the
/// Hidden bit set or cleared per <c>hide</c>, the System bit set or cleared per <c>system</c>, every
/// other attribute left untouched. Lifted out of the <c>apply</c>-mode loop so the core bit math is
/// unit-tested without touching a real file or running on Windows — the <see cref="FileAttributes"/>
/// flags exist on every platform, only their on-disk effect is Windows-specific.
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
