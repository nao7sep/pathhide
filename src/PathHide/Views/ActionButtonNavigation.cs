namespace PathHide.Views;

/// <summary>A navigation key within the action bar.</summary>
public enum ToolbarKey
{
    Previous,
    Next,
    Home,
    End,
}

/// <summary>
/// Pure keyboard-navigation math for the action-button group: from the focused button, Left/Right move
/// to the adjacent one and Home/End jump to the ends, all stopping at the ends rather than letting the
/// key escape the group. Lifted out so the bounded step is unit-tested without building a focusable
/// button group; the window owns the live visible/enabled/focusable filtering and the actual focus move.
/// </summary>
/// <remarks>
/// This is the inner half of the Toolbar pattern: the bar is ONE tab stop (set in the XAML) and these
/// keys move within it. Without the single tab stop, every button was its own stop — and the bar's width
/// is a user preference, so reaching the grid below it cost one more Tab press per configured button.
/// </remarks>
public static class ActionButtonNavigation
{
    /// <summary>Where a navigation key lands, or null to stay put.</summary>
    public static int? Target(ToolbarKey key, int current, int count)
    {
        if (count <= 0)
            return null;

        return key switch
        {
            ToolbarKey.Home => 0,
            ToolbarKey.End => count - 1,
            ToolbarKey.Previous => NextIndex(current, forward: false, count),
            ToolbarKey.Next => NextIndex(current, forward: true, count),
            _ => null,
        };
    }

    /// <summary>
    /// The index to focus next, or null to stay put — when there is no current focus
    /// (<paramref name="current"/> out of range), or it is already at the end in the requested
    /// direction. <paramref name="current"/> is the focused button's index among the navigable buttons;
    /// <paramref name="count"/> is how many there are.
    /// </summary>
    public static int? NextIndex(int current, bool forward, int count)
    {
        if (current < 0 || current >= count)
            return null;

        var next = current + (forward ? 1 : -1);
        return next >= 0 && next < count ? next : null;
    }
}
