using System;
using System.Diagnostics;

namespace PathHide.Services;

/// <summary>
/// Edge helper for opening an external URL in the user's default browser via the OS
/// shell handler. Best-effort and self-contained: a launch failure — most notably the
/// <see cref="System.ComponentModel.Win32Exception"/> thrown when no handler is
/// registered for the scheme — is caught and logged at this boundary rather than
/// bubbling out of a UI click handler as an unhandled exception.
/// </summary>
/// <remarks>
/// This is the one place shelling out to a browser lives, so View click handlers stay
/// free of <see cref="Process"/> and its error handling. It mirrors the guarded,
/// logged shell-out pattern used by <see cref="LogReveal"/> and
/// <see cref="WindowsElevatedApplicator"/>.
/// </remarks>
public static class ExternalLauncher
{
    public static void Open(string url) => Open(url, StartShellExecute, ReportFailure);

    // Test seam: the launch and the failure sink are injected so the boundary contract
    // — a failed launch is reported, never thrown — is observable without a real
    // process or the static Log facade.
    internal static void Open(string url, Action<string> start, Action<string, Exception> onError)
    {
        try
        {
            start(url);
        }
        catch (Exception ex)
        {
            onError(url, ex);
        }
    }

    private static void StartShellExecute(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    private static void ReportFailure(string url, Exception ex) =>
        Log.Error("open external: failed", ex, new { url });
}
