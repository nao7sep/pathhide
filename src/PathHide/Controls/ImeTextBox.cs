using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.TextInput;

namespace PathHide.Controls;

/// <summary>
/// PathHide's free-text control. It preserves stock TextBox editing and default-button behavior while
/// correcting Avalonia.Native's macOS IME candidate-window coordinate mismatch.
/// </summary>
public class ImeTextBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    private ScrollViewer? _scrollViewer;
    private MacOsTextInputMethodClient? _macOsInputClient;

    public ImeTextBox()
    {
        if (OperatingSystem.IsMacOS())
        {
            TextInputMethodClientRequested += OnTextInputMethodClientRequested;
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged -= OnScrollChanged;
        }

        base.OnApplyTemplate(e);
        _scrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");

        if (OperatingSystem.IsMacOS() && _scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += OnScrollChanged;
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) =>
        _macOsInputClient?.NotifyCursorRectangleChanged();

    private void OnTextInputMethodClientRequested(
        object? sender,
        TextInputMethodClientRequestedEventArgs e)
    {
        if (e.Client is null || ReferenceEquals(e.Client, _macOsInputClient))
        {
            return;
        }

        if (_macOsInputClient?.Wraps(e.Client) != true)
        {
            _macOsInputClient = new MacOsTextInputMethodClient(e.Client, this);
        }

        e.Client = _macOsInputClient;
    }
}
