using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace PathHide.Views;

/// <summary>Semantic section a shortcut belongs to; drives the modal's section order and headers.</summary>
public enum ShortcutGroup
{
    Files,
    Visibility,
    List,
    Navigation,
    App,
}

/// <summary>
/// Identifies a window-level command accelerator. The window maps each value to the matching
/// behavior in <c>MainWindow.TryRunShortcut</c>; display-only rows (Up/Down, Delete, drag and
/// drop) carry no action.
/// </summary>
public enum ShortcutAction
{
    AddFiles,
    AddDirectories,
    HideSelected,
    ShowSelected,
    ReapplyAll,
    Reload,
    CancelScan,
    OpenSettings,
    ShowShortcuts,
}

/// <summary>
/// One row of the shortcut catalog. <see cref="Gesture"/> and <see cref="Action"/> are set only
/// for window-level command accelerators, which the window both binds and dispatches; display-only
/// rows describe behavior owned by a control (the grid's Delete, the action group's Left/Right) or a
/// pointer affordance (drag and drop) and carry just the label. <see cref="ShowAsKeycap"/> is true for
/// everything that names a key and false for the non-key affordances rendered as plain text.
/// </summary>
/// <remarks>
/// <see cref="DescriptionKey"/> is always a catalogue key. <see cref="Label"/> is the key legend,
/// shown as written, because keyboard tokens stay English in every language
/// (keyboard-shortcut conventions); for a non-key affordance, which is words rather than a key, it
/// is a catalogue key too.
/// </remarks>
public sealed record ShortcutItem(
    ShortcutGroup Group,
    string DescriptionKey,
    string Label,
    KeyGesture? Gesture = null,
    ShortcutAction? Action = null,
    bool ShowAsKeycap = true);

/// <summary>
/// The single source of truth for PathHide's keyboard shortcuts. Both the live window accelerators
/// and the help modal are derived from one ordered list, so a displayed label can never describe a
/// binding that does not exist. The catalog owns presentation (labels, grouping) and the gesture
/// derivation; it holds no command logic — the window maps each <see cref="ShortcutAction"/> to a
/// command.
/// </summary>
public static class ShortcutCatalog
{
    /// <summary>
    /// The order the shortcuts dialog lays its sections out in, and so the order its two columns
    /// divide. App sits with Files and Navigation rather than at the end: those three are the app and
    /// getting around it, while Visibility and List act on the entries, which divides the card into
    /// two columns that are about the same height as well as about the same subject. Left in the
    /// original order the split can do no better than five rows against eight, which leaves a third
    /// of the left column empty.
    /// </summary>
    public static readonly IReadOnlyList<ShortcutGroup> GroupOrder =
    [
        ShortcutGroup.Files,
        ShortcutGroup.Navigation,
        ShortcutGroup.App,
        ShortcutGroup.Visibility,
        ShortcutGroup.List,
    ];

    /// <summary>The catalogue key of a section's header.</summary>
    public static string GroupHeaderKey(ShortcutGroup group) => group switch
    {
        ShortcutGroup.Files => "shortcuts.groupFiles",
        ShortcutGroup.Visibility => "shortcuts.groupVisibility",
        ShortcutGroup.List => "shortcuts.groupList",
        ShortcutGroup.Navigation => "shortcuts.groupNavigation",
        ShortcutGroup.App => "shortcuts.groupApp",
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, null),
    };

    /// <summary>
    /// The platform command key — <c>Meta</c> (Cmd) on macOS, <c>Control</c> on Windows/Linux. This is
    /// the single place it is resolved, so every accelerator binds the right modifier on every platform.
    /// Defers to the framework's own notion of the command modifier; falls back to <c>Control</c> only
    /// if the platform settings are unavailable.
    /// </summary>
    public static KeyModifiers CommandModifier(TopLevel top) =>
        top.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    /// <summary>
    /// The displayed word for the platform command key — <c>"Cmd"</c> on macOS (where the command
    /// modifier is <c>Meta</c>), <c>"Ctrl"</c> on Windows/Linux. Derived from the same platform signal
    /// as <see cref="CommandModifier"/> so labels show the running platform's single word, never the
    /// combined <c>Cmd/Ctrl</c>.
    /// </summary>
    public static string CommandModifierLabel(TopLevel top) =>
        CommandModifier(top) == KeyModifiers.Meta ? "Cmd" : "Ctrl";

    /// <summary>Builds the ordered catalog.</summary>
    public static IReadOnlyList<ShortcutItem> Build(TopLevel top)
    {
        var cmd = CommandModifier(top);
        var cmdLabel = CommandModifierLabel(top);
        var items = new List<ShortcutItem>
        {
            // Files
            Command(ShortcutGroup.Files, "shortcuts.addFiles", cmd, cmdLabel, shift: false, Key.O, "O", ShortcutAction.AddFiles),
            Command(ShortcutGroup.Files, "shortcuts.addDirectories", cmd, cmdLabel, shift: true, Key.O, "O", ShortcutAction.AddDirectories),
            Display(ShortcutGroup.Files, "shortcuts.drop", "shortcuts.dropLabel", asKeycap: false),

            // Navigation — owned by the action-button group and the grid, listed here for discoverability.
            // Buttons first, then the list, matching their top-to-bottom layout in the window.
            Display(ShortcutGroup.Navigation, "shortcuts.moveFocus", "Left/Right"),
            Display(ShortcutGroup.Navigation, "shortcuts.moveSelection", "Up/Down"),

            // Visibility — Shift on the letter keys avoids the macOS Cmd+H / Cmd+S system collisions.
            Command(ShortcutGroup.Visibility, "shortcuts.hideSelected", cmd, cmdLabel, shift: true, Key.H, "H", ShortcutAction.HideSelected),
            Command(ShortcutGroup.Visibility, "shortcuts.showSelected", cmd, cmdLabel, shift: true, Key.S, "S", ShortcutAction.ShowSelected),
            Command(ShortcutGroup.Visibility, "shortcuts.reapplyAll", cmd, cmdLabel, shift: true, Key.R, "R", ShortcutAction.ReapplyAll),

            // List — scan-lifecycle commands first, the destructive Remove last (mirrors the toolbar's
            // Reload-before-Remove order; Cancel sits with Reload since both act on the scan).
            Command(ShortcutGroup.List, "shortcuts.reload", cmd, cmdLabel, shift: false, Key.R, "R", ShortcutAction.Reload),
            // Escape is a plain-key accelerator (no command modifier), active only while a scan runs.
            new ShortcutItem(ShortcutGroup.List, "shortcuts.cancelScan", "Escape",
                new KeyGesture(Key.Escape), ShortcutAction.CancelScan),
            Display(ShortcutGroup.List, "shortcuts.remove", "Delete"),

            // App. Settings is cross-platform — it was Windows-only until the UI-font
            // setting was added; HasWindowsHideMode is the platform-specific flag now,
            // and it gates a section INSIDE the dialog rather than the dialog itself.
            Command(ShortcutGroup.App, "shortcuts.settings", cmd, cmdLabel, shift: false, Key.OemComma, "Comma", ShortcutAction.OpenSettings),
            Command(ShortcutGroup.App, "shortcuts.shortcuts", cmd, cmdLabel, shift: false, Key.OemQuestion, "Slash", ShortcutAction.ShowShortcuts),
        };

        return items;
    }

    /// <summary>
    /// Builds a command accelerator from one definition so the label and the gesture cannot diverge.
    /// The label uses the running platform's single command word (<paramref name="cmdLabel"/>); only the
    /// gesture's modifier is platform-resolved.
    /// </summary>
    private static ShortcutItem Command(
        ShortcutGroup group, string descriptionKey, KeyModifiers cmd, string cmdLabel, bool shift, Key key, string keyName, ShortcutAction action)
    {
        var label = cmdLabel + "+" + (shift ? "Shift+" : "") + keyName;
        var modifiers = cmd | (shift ? KeyModifiers.Shift : KeyModifiers.None);
        return new ShortcutItem(group, descriptionKey, label, new KeyGesture(key, modifiers), action);
    }

    private static ShortcutItem Display(ShortcutGroup group, string descriptionKey, string label, bool asKeycap = true) =>
        new(group, descriptionKey, label, ShowAsKeycap: asKeycap);
}
