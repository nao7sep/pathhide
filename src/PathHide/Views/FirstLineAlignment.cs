using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;

namespace PathHide.Views;

/// <summary>
/// Centres a top-aligned button, such as a result's close mark, on the first line of the text beside
/// it. The first line's height is read from the text's own layout after every layout pass, so it
/// follows the text, its width, the UI font and the language, including a fallback font that makes
/// Japanese lines taller than Latin ones. Only the button moves: its layout height stays what its
/// style gives it, so the text and the row around it lay out exactly as they would without this.
/// </summary>
internal static class FirstLineAlignment
{
    public static readonly AttachedProperty<TextBlock?> ToProperty =
        AvaloniaProperty.RegisterAttached<Button, TextBlock?>("To", typeof(FirstLineAlignment));

    // The margin the button's style gives it, before this adds its own.
    private static readonly ConditionalWeakTable<Button, StrongBox<Thickness>> s_styledMargin = new();

    static FirstLineAlignment()
    {
        ToProperty.Changed.AddClassHandler<Button>((button, e) =>
        {
            button.LayoutUpdated -= OnLayoutUpdated;
            if (e.NewValue is TextBlock)
                button.LayoutUpdated += OnLayoutUpdated;
        });
    }

    public static TextBlock? GetTo(Button button) => button.GetValue(ToProperty);

    public static void SetTo(Button button, TextBlock? text) => button.SetValue(ToProperty, text);

    private static void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is Button button)
            Align(button);
    }

    private static void Align(Button button)
    {
        if (GetTo(button) is not { } text || button.GetVisualParentOrNull() is not { } parent
            || text.TextLayout.TextLines.Count == 0)
            return;

        var styled = s_styledMargin.GetValue(button, b => new StrongBox<Thickness>(b.Margin)).Value;
        var current = button.Margin;
        if (text.TranslatePoint(new Point(0, text.Padding.Top), parent) is not { } textTop)
            return;

        // The button's slot starts where it would sit with no top margin; its centre goes to the first
        // line's centre, and the bottom margin gives back what the top takes, so its layout height is
        // unchanged.
        var slotTop = button.Bounds.Y - current.Top;
        var firstLineCentre = textTop.Y + text.TextLayout.TextLines[0].Height / 2;
        // On the device pixel grid, as layout rounding places the button, so the next pass reads back
        // the margin it was given and the alignment settles instead of chasing a rounded position.
        var scale = TopLevel.GetTopLevel(button)?.RenderScaling ?? 1;
        var top = Math.Round((firstLineCentre - button.Bounds.Height / 2 - slotTop) * scale) / scale;
        if (Math.Abs(top - current.Top) < 0.01)
            return;
        button.Margin = new Thickness(styled.Left, top, styled.Right, styled.Top + styled.Bottom - top);
    }

    private static Visual? GetVisualParentOrNull(this Visual visual) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualParent(visual);
}
