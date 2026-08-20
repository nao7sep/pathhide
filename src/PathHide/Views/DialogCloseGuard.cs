using Avalonia.Controls;
using Avalonia.Input;

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

    /// <summary>
    /// Whether a key press should dismiss the dialog.
    /// </summary>
    /// <remarks>
    /// Escape dismisses — routed through the close guard, so a dirty dialog still gets its
    /// discard confirmation. A key the input method already consumed does not: Escape
    /// mid-conversion is the IME's own cancel gesture, and acting on it would close the dialog
    /// out from under a half-typed Japanese value, raising the discard prompt on top since the
    /// draft reads dirty.
    /// </remarks>
    public static bool ShouldDismissOnKey(Key key) => key == Key.Escape;
}
