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
}
