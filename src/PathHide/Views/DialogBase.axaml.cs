using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace PathHide.Views;

/// <summary>How a footer button looks and whether closing through it commits the dialog.</summary>
public enum DialogButtonKind
{
    /// <summary>Neutral secondary action (Cancel, No, Close). Dismisses — runs the discard guard.</summary>
    Normal,

    /// <summary>Accent-styled primary action (Save, OK). Commits — bypasses the discard guard.</summary>
    Primary,

    /// <summary>Danger-styled destructive action (Remove, Discard, Delete). Commits.</summary>
    Danger,
}

/// <summary>
/// A footer button for <see cref="DialogBase"/>. <see cref="Tag"/> is surfaced through
/// <see cref="DialogBase.ResultTag"/> when the button is activated. Exactly one button per
/// dialog should set <see cref="IsDefault"/>; that button is Enter-activated and should be
/// the safest action (the commit for a benign form, Cancel for a destructive confirm).
/// </summary>
public sealed record DialogButton(string Label, string Tag, DialogButtonKind Kind = DialogButtonKind.Normal)
{
    public bool IsDefault { get; init; }
}

/// <summary>
/// Shared modal shell for app-controlled dialogs. Owns chrome and mechanics only:
/// header/body/footer layout, button styling, initial focus, Enter/Escape routing, and the
/// single close guard. Feature dialogs own their data, validation, and dirty state.
/// </summary>
/// <remarks>
/// Every close path — Escape, the title-bar close button, a Cancel button, a commit button,
/// programmatic <see cref="Window.Close()"/>, owner close, and OS shutdown — funnels through
/// <see cref="OnClosing"/>. Only a direct user dismiss of a dialog that still reports
/// <see cref="HasUnsavedChanges"/> is intercepted to confirm discarding the draft. Commit
/// closes, owner close, app shutdown, and OS shutdown never block.
/// </remarks>
public partial class DialogBase : Window
{
    private readonly HashSet<Button> _commitButtons = [];
    private Control? _initialFocusControl;
    private bool _bypassCloseGuard;

    /// <summary>Tag of the button the user activated, or <c>null</c> if the dialog was dismissed.</summary>
    public string? ResultTag { get; private set; }

    public DialogBase()
    {
        InitializeComponent();
        Activated += (_, _) => Classes.Set("windowInactive", false);
        Deactivated += (_, _) => Classes.Set("windowInactive", true);
        Opened += OnOpened;
        KeyDown += OnKeyDown;
        Closing += OnClosing;
    }

    /// <summary>
    /// Overridden by dialogs with an editable draft to report whether the draft differs from
    /// its committed baseline. Default: nothing to lose, so the close guard never prompts.
    /// </summary>
    protected virtual bool HasUnsavedChanges => false;

    /// <summary>Discard-confirmation copy used when a dirty dialog is dismissed.</summary>
    protected virtual (string Title, string Message) DiscardPrompt =>
        ("Discard Changes", "You have unsaved changes. Discard them and close?");

    protected void SetContent(Control content) => DialogContent.Content = content;

    /// <summary>
    /// Builds the footer. Buttons render left to right in the order given, right aligned, so
    /// callers pass secondary/cancel actions before primary/destructive ones. Primary and
    /// danger buttons are treated as commit actions: closing through them bypasses the discard
    /// guard because the caller is about to consume <see cref="ResultTag"/>.
    /// </summary>
    protected IReadOnlyDictionary<string, Button> SetButtons(IEnumerable<DialogButton> buttons)
    {
        ButtonPanel.Children.Clear();
        _commitButtons.Clear();
        var created = new Dictionary<string, Button>();

        foreach (var descriptor in buttons)
        {
            var button = new Button
            {
                Content = descriptor.Label,
                Tag = descriptor.Tag,
                MinWidth = 80,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                IsDefault = descriptor.IsDefault,
            };

            switch (descriptor.Kind)
            {
                case DialogButtonKind.Primary:
                    button.Classes.Add("accent");
                    _commitButtons.Add(button);
                    break;
                case DialogButtonKind.Danger:
                    button.Classes.Add("destructive");
                    _commitButtons.Add(button);
                    break;
            }

            button.Click += OnButtonClick;
            ButtonPanel.Children.Add(button);
            created[descriptor.Tag] = button;
        }

        return created;
    }

    protected void SetInitialFocus(Control control) => _initialFocusControl = control;

    /// <summary>Allows a feature dialog to persist its draft before the shell closes.</summary>
    protected virtual bool TryCommit(string tag) => true;

    private void OnOpened(object? sender, EventArgs e)
    {
        ClampHeightToScreen();

        var target = _initialFocusControl;
        if (target is null)
            return;

        Dispatcher.UIThread.Post(() => target.Focus());
    }

    /// <summary>
    /// Bounds the dialog to the working area of the screen it opened on, so
    /// SizeToContent can never grow it past what fits.
    /// </summary>
    /// <remarks>
    /// Derived rather than a fixed number: what fits depends on the display,
    /// and the body's height depends on the user's UI font. Without this the
    /// docked footer — every dismiss path this shell offers — is pushed off the
    /// bottom of a small laptop screen, on a window that cannot be resized.
    /// </remarks>
    private void ClampHeightToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;

        // WorkingArea is in physical pixels; MaxHeight is in logical ones.
        var available = screen.WorkingArea.Height / screen.Scaling;

        // Leave a margin so the dialog reads as a window on the desktop rather
        // than something wedged against both edges.
        MaxHeight = available * 0.9;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // The policy lives in DialogCloseGuard so it is testable; the window only acts on it.
        // Escape is just another dismiss path — routed through the close guard rather than
        // closing directly, so a dirty dialog still gets its discard confirmation — while a key
        // the input method already consumed belongs to the composition and is left alone.
        if (DialogCloseGuard.ShouldDismissOnKey(e.Key))
            Close();
    }

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        var tag = button.Tag as string;
        if (tag is null || (_commitButtons.Contains(button) && !TryCommit(tag)))
            return;

        ResultTag = tag;
        if (_commitButtons.Contains(button))
            _bypassCloseGuard = true;

        Close();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!DialogCloseGuard.ShouldConfirmDiscard(e.CloseReason, _bypassCloseGuard, HasUnsavedChanges))
            return;

        // Hold the dialog open and ask. e.Cancel is set synchronously, before the first await,
        // so the in-progress close is cancelled deterministically.
        e.Cancel = true;
        var (title, message) = DiscardPrompt;
        if (await ConfirmDialog.ConfirmDestructiveAsync(this, title, message, "Discard"))
        {
            _bypassCloseGuard = true;
            Close();
        }
    }
}
