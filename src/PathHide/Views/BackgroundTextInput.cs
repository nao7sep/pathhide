using System;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using PathHide.Services;

namespace PathHide.Views;

/// <summary>
/// Text that reaches a window while it is in the background goes to the text field that had focus
/// there. The macOS emoji picker (Edit › Emoji &amp; Symbols, Control-Command-Space, the Globe key)
/// takes the keyboard while it is open and hands its choice back afterwards, and since Avalonia
/// 11.3.1 a window that loses the keyboard also drops its focused element (Avalonia PR 18990, issue
/// 20616). The emoji then reaches the window itself and is lost; Avalonia refocuses the field only
/// once the window is in front again. A window in front with nothing focused is left alone: keys
/// typed into it go nowhere, as before. macOS only: the defect is in Avalonia's macOS backend, and on
/// Windows the emoji panel inserts into the focused field without help.
/// </summary>
internal static class BackgroundTextInput
{
    private static readonly ConditionalWeakTable<TopLevel, WeakReference<TextBox>> s_lastField = new();

    // Whether a window is in front; tests replace it, since a headless window cannot be put behind.
    internal static Func<WindowBase, bool> IsInFront = window => window.IsActive;

    /// <summary>Whether <see cref="Install"/> has installed the handler in this process.</summary>
    internal static bool Installed { get; private set; }

    /// <summary>
    /// Starts remembering each window's text field and handing it background text. Does nothing off
    /// macOS.
    /// </summary>
    public static void Install()
    {
        if (!OperatingSystem.IsMacOS() || Installed)
            return;
        Installed = true;
        InputElement.GotFocusEvent.AddClassHandler<TextBox>((field, _) => Remember(field));
        InputElement.TextInputEvent.AddClassHandler<WindowBase>(Deliver);
    }

    private static void Remember(TextBox field)
    {
        if (TopLevel.GetTopLevel(field) is { } window)
            s_lastField.AddOrUpdate(window, new WeakReference<TextBox>(field));
    }

    // Hands text raised on the window itself, which is where Avalonia sends text that arrives with
    // nothing focused, to the window's last text field while the window is in the background.
    private static void Deliver(WindowBase window, TextInputEventArgs e)
    {
        try
        {
            if (e.Handled || IsInFront(window) || !ReferenceEquals(e.Source, window) || string.IsNullOrEmpty(e.Text))
                return;

            // A read-only field takes the text and ignores it, as it would typed text.
            if (!s_lastField.TryGetValue(window, out var last)
                || !last.TryGetTarget(out var field)
                || TopLevel.GetTopLevel(field) != window
                || !field.Focus())
            {
                // The length only: what was typed is never logged.
                Log.Info("ui: text reached a background window with no text field to take it", new { length = e.Text.Length });
                return;
            }

            field.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = e.Text });
            e.Handled = true;
            Log.Info("ui: text that reached a background window went to its text field", new { length = e.Text.Length });
        }
        catch (Exception ex)
        {
            // A fault in this workaround must never break typing; the text stays where Avalonia put it.
            Log.Error("ui: background text could not be handed to its text field", ex);
        }
    }
}
