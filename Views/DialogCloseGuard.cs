using Avalonia.Controls;

namespace PathHide.Views;

/// <summary>
/// The close-guard decision shared by every <see cref="DialogBase"/>: should a pending
/// close be intercepted to confirm discarding unsaved draft state?
/// </summary>
/// <remarks>
/// This encodes the three close modes from the modal conventions:
/// <list type="bullet">
/// <item>A direct user dismiss of a dialog that still has unsaved changes is intercepted
/// so the user can confirm losing the draft.</item>
/// <item>A commit close (Save/Apply) has already captured the user's intent, so it never
/// prompts.</item>
/// <item>Owner close, app shutdown, and OS session shutdown must never block: they take the
/// discard/no-op direction automatically and let the close proceed.</item>
/// </list>
/// Kept as a pure function so the close-mode policy can be tested without a UI thread.
/// </remarks>
public static class DialogCloseGuard
{
    public static bool ShouldConfirmDiscard(WindowCloseReason reason, bool committing, bool hasUnsavedChanges)
        => !committing
           && reason == WindowCloseReason.WindowClosing
           && hasUnsavedChanges;
}
