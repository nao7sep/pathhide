using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

/// <summary>
/// Which closes of the main window are the operating system ending the session, which never asks
/// anything, and which are quits the user started (unsaved-edits-conventions, Quitting).
/// </summary>
public sealed class SessionEndTests
{
    [Theory]
    // A Windows shutdown or restart, and a Windows logoff, which Avalonia reports as an app shutdown.
    [InlineData(WindowCloseReason.OSShutdown, true, false, true)]
    [InlineData(WindowCloseReason.ApplicationShutdown, true, false, true)]
    // macOS: a logout, restart or shutdown carries its reason on the quit event; the Dock's quit and the
    // menu's Quit and Cmd+Q do not.
    [InlineData(WindowCloseReason.ApplicationShutdown, false, true, true)]
    [InlineData(WindowCloseReason.ApplicationShutdown, false, false, false)]
    // The window's own close button or shortcut, on either system.
    [InlineData(WindowCloseReason.WindowClosing, true, false, false)]
    [InlineData(WindowCloseReason.WindowClosing, false, true, false)]
    [InlineData(WindowCloseReason.Undefined, false, false, false)]
    public void Only_the_system_ending_the_session_is_a_session_end(
        WindowCloseReason reason, bool windows, bool macQuitEventHasReason, bool sessionEnd) =>
        Assert.Equal(sessionEnd, SessionEnd.Is(reason, windows, () => macQuitEventHasReason));

    [Fact]
    public void The_quit_event_is_read_only_for_a_shutdown_the_lifetime_reports()
    {
        var read = 0;
        SessionEnd.Is(WindowCloseReason.WindowClosing, windows: false, () => { read++; return true; });
        SessionEnd.Is(WindowCloseReason.OSShutdown, windows: false, () => { read++; return true; });
        Assert.Equal(0, read);
    }

    // macOS: loginwindow's quit event at logout, restart or shutdown carries keyAEQuitReason; a quit from
    // the Dock or another app carries none. Both are built here as AppKit receives them.
    [MacOnlyFact]
    public void On_macOS_a_quit_event_with_a_reason_is_a_session_end_and_one_without_is_not()
    {
        NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
        var dockQuit = QuitEvent();
        var logoutQuit = QuitEvent();
        try
        {
            var logOut = ObjC.SendWithCode(ObjC.Class("NSAppleEventDescriptor"), "descriptorWithEnumCode:", Code("logo"));
            SetAttribute(logoutQuit, ObjC.Sel("setAttributeDescriptor:forKeyword:"), logOut, Code("why?"));

            Assert.False(SessionEnd.QuitEventCarriesReason(IntPtr.Zero));
            Assert.False(SessionEnd.QuitEventCarriesReason(dockQuit));
            Assert.True(SessionEnd.QuitEventCarriesReason(logoutQuit));
        }
        finally
        {
            ObjC.Send(dockQuit, "release");
            ObjC.Send(logoutQuit, "release");
        }
    }

    private static uint Code(string fourChars) =>
        (uint)(fourChars[0] << 24 | fourChars[1] << 16 | fourChars[2] << 8 | fourChars[3]);

    // A kCoreEventClass/kAEQuitApplication event with no target, an automatic return ID and any transaction.
    private static IntPtr QuitEvent()
    {
        var allocated = ObjC.Send(ObjC.Class("NSAppleEventDescriptor"), "alloc");
        var quit = InitEvent(allocated, ObjC.Sel("initWithEventClass:eventID:targetDescriptor:returnID:transactionID:"),
            Code("aevt"), Code("quit"), IntPtr.Zero, -1, 0);
        Assert.NotEqual(IntPtr.Zero, quit);
        return quit;
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr InitEvent(IntPtr receiver, IntPtr selector, uint eventClass, uint eventId,
        IntPtr target, short returnId, int transactionId);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SetAttribute(IntPtr receiver, IntPtr selector, IntPtr descriptor, uint keyword);
}
