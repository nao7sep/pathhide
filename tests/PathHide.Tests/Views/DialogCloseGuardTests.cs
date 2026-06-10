using Avalonia.Controls;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

/// <summary>
/// The close-mode policy for app dialogs: only a direct user dismiss of a dirty dialog may be
/// intercepted to confirm discarding the draft. Every other close — committing, owner close,
/// app shutdown, and OS session shutdown — must proceed without blocking. These cases pin the
/// OS-shutdown rule in particular: a dirty dialog must not stall a shutdown to ask a question.
/// </summary>
public sealed class DialogCloseGuardTests
{
    [Fact]
    public void UserClose_WhenDirty_ConfirmsDiscard()
    {
        Assert.True(DialogCloseGuard.ShouldConfirmDiscard(
            WindowCloseReason.WindowClosing, committing: false, hasUnsavedChanges: true));
    }

    [Fact]
    public void UserClose_WhenClean_DoesNotPrompt()
    {
        Assert.False(DialogCloseGuard.ShouldConfirmDiscard(
            WindowCloseReason.WindowClosing, committing: false, hasUnsavedChanges: false));
    }

    [Fact]
    public void CommitClose_WhenDirty_DoesNotPrompt()
    {
        // Save/Apply has already captured the user's intent — the caller consumes the result,
        // so re-prompting to "discard" the very change being committed would be nonsense.
        Assert.False(DialogCloseGuard.ShouldConfirmDiscard(
            WindowCloseReason.WindowClosing, committing: true, hasUnsavedChanges: true));
    }

    [Theory]
    [InlineData(WindowCloseReason.OSShutdown)]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    [InlineData(WindowCloseReason.OwnerWindowClosing)]
    public void NonUserClose_WhenDirty_NeverBlocks(WindowCloseReason reason)
    {
        // OS shutdown/logout, app quit, and owner-window close must take the discard/no-op
        // direction automatically; the dialog must not hold them open for a confirmation.
        Assert.False(DialogCloseGuard.ShouldConfirmDiscard(
            reason, committing: false, hasUnsavedChanges: true));
    }
}
