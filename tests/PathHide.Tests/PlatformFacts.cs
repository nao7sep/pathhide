using System.Runtime.CompilerServices;
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
    // xUnit v3 (xUnit3003) reads source location from the base constructor's
    // caller arguments so the skipped/failing test points back to its own line.
    public MacOnlyFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Skip = "Runs on macOS only.";
    }
}

/// <summary>A <see cref="FactAttribute"/> that runs only on Windows.</summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Skip = "Runs on Windows only.";
    }
}
