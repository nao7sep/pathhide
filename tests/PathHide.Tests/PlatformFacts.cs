using System.Runtime.InteropServices;
using Xunit;

namespace PathHide.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that runs only on macOS. On other platforms the
/// test is reported as skipped rather than failing, so the suite stays green on
/// Windows/CI while still covering the mac-specific syscall path on a real mac.
/// </summary>
public sealed class MacOnlyFactAttribute : FactAttribute
{
    public MacOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Skip = "Runs on macOS only.";
    }
}

/// <summary>A <see cref="FactAttribute"/> that runs only on Windows.</summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Skip = "Runs on Windows only.";
    }
}
