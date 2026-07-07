namespace PathHide.Views;

/// <summary>
/// Pure arrow-key navigation math for the action-button group: from the focused button, Left/Right move
/// to the adjacent one and stop at the ends rather than letting focus escape the group. Lifted out so the
/// bounded step is unit-tested without building a focusable button group; the window owns the live
/// visible/enabled/focusable filtering and the actual focus move.
/// </summary>
public static class ActionButtonNavigation
{
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
