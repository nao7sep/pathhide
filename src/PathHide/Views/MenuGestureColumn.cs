using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace PathHide.Views;

/// <summary>
/// A menu item without a keyboard gesture takes no room for one. Fluent's menu item template leads
/// its gesture text with a 24px margin whether or not the item has a gesture, so a menu of plain
/// items ends in 24px of empty space after its longest label. Here that margin is set to zero, as a
/// local value, on every item without a gesture; an item with one, such as a text field's Cut, keeps
/// the template's margin between its label and its gesture.
///
/// A style cannot do this: the template sets the margin at a priority above every style. Nor can the
/// template's <c>MenuInputGestureTextMargin</c> resource: it applies to every item, and would leave a
/// text field's Cut and its gesture touching.
/// </summary>
internal static class MenuGestureColumn
{
    private const string GestureTextPart = "PART_InputGestureText";

    private static bool s_installed;

    /// <summary>Applies the rule to every menu item in the process, now and as gestures change.</summary>
    public static void Install()
    {
        if (s_installed)
            return;
        s_installed = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<MenuItem>(
            (item, e) => Apply(item, e.NameScope.Find<TextBlock>(GestureTextPart)));
        MenuItem.InputGestureProperty.Changed.AddClassHandler<MenuItem>(
            (item, _) => Apply(item, item.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(text => text.Name == GestureTextPart && text.TemplatedParent == item)));
    }

    // A top-level menu bar item's template has no gesture text, so it is left alone.
    private static void Apply(MenuItem item, TextBlock? gestureText)
    {
        if (gestureText is null)
            return;
        if (item.InputGesture is null)
            gestureText.Margin = default;
        else
            gestureText.ClearValue(Avalonia.Layout.Layoutable.MarginProperty);
    }
}
