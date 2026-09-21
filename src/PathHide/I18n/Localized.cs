using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace PathHide.I18n;

/// <summary>
/// How a control gets its words: a key instead of a sentence.
///
/// <code>
/// &lt;TextBlock i18n:Localized.Text="grid.empty" /&gt;
/// &lt;Button i18n:Localized.Content="toolbar.reload" i18n:Localized.ToolTip="menu.button" /&gt;
/// </code>
///
/// The control keeps the key, not the words, so every assignment is re-applied when the language
/// changes and no window has to be rebuilt. Code-built controls — which is most of this app's
/// dialogs — call <see cref="SetText"/> and its neighbours for the same effect.
///
/// This is an attached property rather than a binding on a translator object because the words must
/// follow a language change in surfaces that are assigned once in a constructor and never bound at
/// all; a key held here reaches those the same way it reaches markup.
/// </summary>
internal static class Localized
{
    /// <summary>The key whose words fill the control's own <c>Text</c>.</summary>
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, string?>("Text", typeof(Localized));

    /// <summary>The key whose words fill the control's own <c>Content</c>, as on a button.</summary>
    public static readonly AttachedProperty<string?> ContentProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, string?>("Content", typeof(Localized));

    /// <summary>The key whose words fill the control's own <c>Header</c>, as on a menu item.</summary>
    public static readonly AttachedProperty<string?> HeaderProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, string?>("Header", typeof(Localized));

    /// <summary>The key whose words fill a window's title.</summary>
    public static readonly AttachedProperty<string?> TitleProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, string?>("Title", typeof(Localized));

    /// <summary>The key whose words fill a text box's placeholder.</summary>
    public static readonly AttachedProperty<string?> PlaceholderTextProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, string?>("PlaceholderText", typeof(Localized));

    /// <summary>The key whose words are the control's tooltip.</summary>
    public static readonly AttachedProperty<string?> ToolTipProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, string?>("ToolTip", typeof(Localized));

    /// <summary>The key whose words a screen reader announces as the control's name.</summary>
    public static readonly AttachedProperty<string?> AutomationNameProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, string?>("AutomationName", typeof(Localized));

    /// <summary>The key whose words a screen reader reads as the control's help text.</summary>
    public static readonly AttachedProperty<string?> AutomationHelpTextProperty =
        AvaloniaProperty.RegisterAttached<AvaloniaObject, string?>("AutomationHelpText", typeof(Localized));

    public static void SetText(AvaloniaObject target, string? key) => target.SetValue(TextProperty, key);
    public static string? GetText(AvaloniaObject target) => target.GetValue(TextProperty);

    public static void SetContent(AvaloniaObject target, string? key) => target.SetValue(ContentProperty, key);
    public static string? GetContent(AvaloniaObject target) => target.GetValue(ContentProperty);

    public static void SetHeader(AvaloniaObject target, string? key) => target.SetValue(HeaderProperty, key);
    public static string? GetHeader(AvaloniaObject target) => target.GetValue(HeaderProperty);

    public static void SetTitle(AvaloniaObject target, string? key) => target.SetValue(TitleProperty, key);
    public static string? GetTitle(AvaloniaObject target) => target.GetValue(TitleProperty);

    public static void SetPlaceholderText(AvaloniaObject target, string? key) =>
        target.SetValue(PlaceholderTextProperty, key);
    public static string? GetPlaceholderText(AvaloniaObject target) => target.GetValue(PlaceholderTextProperty);

    public static void SetToolTip(AvaloniaObject target, string? key) => target.SetValue(ToolTipProperty, key);
    public static string? GetToolTip(AvaloniaObject target) => target.GetValue(ToolTipProperty);

    public static void SetAutomationName(AvaloniaObject target, string? key) =>
        target.SetValue(AutomationNameProperty, key);
    public static string? GetAutomationName(AvaloniaObject target) => target.GetValue(AutomationNameProperty);

    public static void SetAutomationHelpText(AvaloniaObject target, string? key) =>
        target.SetValue(AutomationHelpTextProperty, key);
    public static string? GetAutomationHelpText(AvaloniaObject target) =>
        target.GetValue(AutomationHelpTextProperty);

    /// <summary>Where each attached key writes its words: a property of the control, by name, or an
    /// attached property of Avalonia's own.</summary>
    private static readonly (AttachedProperty<string?> Key, string? ByName, AvaloniaProperty? Attached)[] s_targets =
    [
        (TextProperty, "Text", null),
        (ContentProperty, "Content", null),
        (HeaderProperty, "Header", null),
        (TitleProperty, "Title", null),
        (PlaceholderTextProperty, "PlaceholderText", null),
        (ToolTipProperty, null, ToolTip.TipProperty),
        (AutomationNameProperty, null, AutomationProperties.NameProperty),
        (AutomationHelpTextProperty, null, AutomationProperties.HelpTextProperty),
    ];

    // Every control that holds a key, so a language change can reach it. The table holds its controls
    // weakly, so a closed window's controls are collected as they always were. The value is one shared
    // marker: a value that referred to its own control would keep that control alive forever. Weak is
    // not the same as gone, which is why RetranslateAll asks each holder whether it is still on screen.
    private static readonly ConditionalWeakTable<AvaloniaObject, object> s_holders = new();
    private static readonly object s_holdsAKey = new();

    static Localized()
    {
        foreach (var (key, _, _) in s_targets)
            key.Changed.AddClassHandler<AvaloniaObject, string?>((target, _) => Apply(target));

        Localizer.Changed += RetranslateAll;
    }

    /// <summary>
    /// Writes the current language's words into every property of <paramref name="target"/> that holds
    /// a key. Public for the code-built surfaces that add their controls after a language change.
    /// </summary>
    internal static void Apply(AvaloniaObject target)
    {
        var holdsKey = false;
        foreach (var (key, byName, attached) in s_targets)
        {
            if (target.GetValue(key) is not { } catalogueKey)
                continue;

            holdsKey = true;
            var property = attached ?? Find(target, byName!);
            if (property is null)
                continue;
            target.SetValue(property, Localizer.T(catalogueKey));
        }

        if (!holdsKey || s_holders.TryGetValue(target, out _))
            return;

        s_holders.AddOrUpdate(target, s_holdsAKey);

        // A visual that is off the tree is skipped by RetranslateAll, so it must be brought up to
        // date when it comes back: a control built under one language, or hidden across a change,
        // otherwise returns to the screen still speaking the old one.
        if (target is Visual visual)
            visual.AttachedToVisualTree += (holder, _) => Apply((AvaloniaObject)holder!);
    }

    private static AvaloniaProperty? Find(AvaloniaObject target, string name) =>
        AvaloniaPropertyRegistry.Instance.FindRegistered(target, name);

    private static void RetranslateAll()
    {
        // A copy first: applying a key can register a control, and the table must not change under the
        // walk.
        var holders = new List<AvaloniaObject>();
        foreach (var entry in s_holders)
            holders.Add(entry.Key);

        foreach (var holder in holders)
        {
            // A visual that has left the tree — a closed window and everything it held — is nobody's
            // text any more, and it is not inert either: its template bindings still listen, and the
            // glyph runs behind its text were disposed with its window, so writing words into it now
            // throws rather than translating anything. Attachment brings it back, above.
            if (holder is Visual visual && !visual.IsAttachedToVisualTree())
                continue;
            Apply(holder);
        }
    }
}
