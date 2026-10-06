using System;
using Avalonia.Controls;
using PathHide.Services;

namespace PathHide.Views;

/// <summary>
/// Whether a close of the main window is the operating system ending the session (logout, restart or
/// shutdown) rather than a quit the user started. The system waits for the answer, so that close never
/// asks anything (unsaved-edits-conventions, Quitting).
/// </summary>
internal static class SessionEnd
{
    // keyAEQuitReason, 'why?': the attribute loginwindow puts on the quit Apple event it sends at logout,
    // restart and shutdown. A quit from the Dock or another app carries none.
    private const uint QuitReasonKeyword = 0x7768793F;

    internal static bool Is(WindowCloseReason reason) =>
        Is(reason, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS() ? MacQuitIsSessionEnd : () => false);

    /// <summary>
    /// Avalonia reports a Windows shutdown or restart as <see cref="WindowCloseReason.OSShutdown"/> and a
    /// Windows logoff as <see cref="WindowCloseReason.ApplicationShutdown"/>; on Windows PathHide starts no
    /// application shutdown of its own while the main window is open, so either one is the session
    /// ending. On macOS every quit the system delivers, the Dock's included, arrives as
    /// <see cref="WindowCloseReason.ApplicationShutdown"/>, and the quit event itself tells them apart.
    /// </summary>
    internal static bool Is(WindowCloseReason reason, bool windows, Func<bool> macQuitIsSessionEnd) => reason switch
    {
        WindowCloseReason.OSShutdown => true,
        WindowCloseReason.ApplicationShutdown => windows || macQuitIsSessionEnd(),
        _ => false,
    };

    // Read while AppKit is still handling the quit event, which is current only until the close returns.
    private static bool MacQuitIsSessionEnd()
    {
        try
        {
            var manager = ObjC.Send(ObjC.Class("NSAppleEventManager"), "sharedAppleEventManager");
            var quitEvent = ObjC.Send(manager, "currentAppleEvent");
            return quitEvent != IntPtr.Zero
                && ObjC.SendWithCode(quitEvent, "attributeDescriptorForKeyword:", QuitReasonKeyword) != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Log.Warn("shutdown: could not read why macOS asked PathHide to quit", ex);
            return false;
        }
    }
}
