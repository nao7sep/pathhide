using System;
using Avalonia;
using Avalonia.Input.TextInput;

namespace PathHide.Controls;

/// <summary>
/// Adapts Avalonia's macOS text-input client so its cursor rectangle and text-view visual use the
/// same coordinate space. Avalonia 12's TextBox client returns a TextBox-relative rectangle, while
/// Avalonia.Native otherwise treats that rectangle as relative to the inner TextPresenter. The
/// mismatch applies the presenter's padding and scroll offset twice when positioning IME candidates.
/// </summary>
internal sealed class MacOsTextInputMethodClient : TextInputMethodClient
{
    private readonly TextInputMethodClient _inner;
    private readonly Visual _coordinateVisual;

    public MacOsTextInputMethodClient(TextInputMethodClient inner, Visual coordinateVisual)
    {
        _inner = inner;
        _coordinateVisual = coordinateVisual;

        _inner.TextViewVisualChanged += InnerTextViewVisualChanged;
        _inner.CursorRectangleChanged += InnerCursorRectangleChanged;
        _inner.SurroundingTextChanged += InnerSurroundingTextChanged;
        _inner.SelectionChanged += InnerSelectionChanged;
        _inner.ResetRequested += InnerResetRequested;
        _inner.InputPaneActivationRequested += InnerInputPaneActivationRequested;
    }

    public override Visual TextViewVisual => _coordinateVisual;

    public override bool SupportsPreedit => _inner.SupportsPreedit;

    public override bool SupportsSurroundingText => _inner.SupportsSurroundingText;

    public override string SurroundingText => _inner.SurroundingText;

    public override Rect CursorRectangle => _inner.CursorRectangle;

    public override TextSelection Selection
    {
        get => _inner.Selection;
        set => _inner.Selection = value;
    }

    public bool Wraps(TextInputMethodClient client) => ReferenceEquals(_inner, client);

    public void NotifyCursorRectangleChanged() => RaiseCursorRectangleChanged();

    public override void SetPreeditText(string? preeditText) => _inner.SetPreeditText(preeditText);

    public override void SetPreeditText(string? preeditText, int? cursorPos) =>
        _inner.SetPreeditText(preeditText, cursorPos);

    public override void ExecuteContextMenuAction(ContextMenuAction action) =>
        _inner.ExecuteContextMenuAction(action);

    private void InnerTextViewVisualChanged(object? sender, EventArgs e) => RaiseTextViewVisualChanged();

    private void InnerCursorRectangleChanged(object? sender, EventArgs e) => RaiseCursorRectangleChanged();

    private void InnerSurroundingTextChanged(object? sender, EventArgs e) => RaiseSurroundingTextChanged();

    private void InnerSelectionChanged(object? sender, EventArgs e) => RaiseSelectionChanged();

    private void InnerResetRequested(object? sender, EventArgs e) => RequestReset();

    private void InnerInputPaneActivationRequested(object? sender, EventArgs e) =>
        RaiseInputPaneActivationRequested();
}
