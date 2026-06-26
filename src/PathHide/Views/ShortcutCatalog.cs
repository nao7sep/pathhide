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
public sealed record ShortcutItem(
    ShortcutGroup Group,
    string Description,
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
    /// <summary>Section order for the help modal; only non-empty groups render.</summary>
    public static readonly IReadOnlyList<ShortcutGroup> GroupOrder =
    [
        ShortcutGroup.Files,
        ShortcutGroup.Navigation,
        ShortcutGroup.Visibility,
        ShortcutGroup.List,
        ShortcutGroup.App,
    ];

    public static string GroupHeader(ShortcutGroup group) => group switch
    {
        ShortcutGroup.Files => "Files",
        ShortcutGroup.Visibility => "Visibility",
        ShortcutGroup.List => "List",
        ShortcutGroup.Navigation => "Navigation",
        ShortcutGroup.App => "App",
        _ => group.ToString(),
    };

    /// <summary>
    /// The platform command key — <c>Meta</c> (Cmd) on macOS, <c>Control</c> on Windows/Linux. This is
    /// the single place it is resolved, so every accelerator binds the right modifier on every platform
    /// while the labels stay the universal <c>Cmd/Ctrl+…</c>. Defers to the framework's own notion of the
    /// command modifier; falls back to <c>Control</c> only if the platform settings are unavailable.
    /// </summary>
    public static KeyModifiers CommandModifier(TopLevel top) =>
        top.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    /// <summary>
    /// Builds the ordered catalog. The Windows-only Settings row is omitted entirely when
    /// <paramref name="hasSettings"/> is false, so macOS never advertises an unreachable shortcut.
    /// </summary>
    public static IReadOnlyList<ShortcutItem> Build(TopLevel top, bool hasSettings)
    {
        var cmd = CommandModifier(top);
        var items = new List<ShortcutItem>
        {
            // Files
            Command(ShortcutGroup.Files, "Add files", cmd, shift: false, Key.O, "O", ShortcutAction.AddFiles),
            Command(ShortcutGroup.Files, "Add directories", cmd, shift: true, Key.O, "O", ShortcutAction.AddDirectories),
            Display(ShortcutGroup.Files, "Add by dropping files or directories on the window", "Drag and drop", asKeycap: false),

            // Navigation — owned by the action-button group and the grid, listed here for discoverability.
            // Buttons first, then the list, matching their top-to-bottom layout in the window.
            Display(ShortcutGroup.Navigation, "Move focus between the action buttons", "Left / Right"),
            Display(ShortcutGroup.Navigation, "Move the selection up or down the list", "Up / Down"),

            // Visibility — Shift on the letter keys avoids the macOS Cmd+H / Cmd+S system collisions.
            Command(ShortcutGroup.Visibility, "Hide the selected entries", cmd, shift: true, Key.H, "H", ShortcutAction.HideSelected),
            Command(ShortcutGroup.Visibility, "Show the selected entries", cmd, shift: true, Key.S, "S", ShortcutAction.ShowSelected),
            Command(ShortcutGroup.Visibility, "Reapply the desired visibility to every entry", cmd, shift: true, Key.R, "R", ShortcutAction.ReapplyAll),

            // List — scan-lifecycle commands first, the destructive Remove last (mirrors the toolbar's
            // Reload-before-Remove order; Cancel sits with Reload since both act on the scan).
            Command(ShortcutGroup.List, "Reload entries and rescan disk state", cmd, shift: false, Key.R, "R", ShortcutAction.Reload),
            // Esc is a plain-key accelerator (no command modifier), active only while a scan runs.
            new ShortcutItem(ShortcutGroup.List, "Cancel the running scan", "Esc",
                new KeyGesture(Key.Escape), ShortcutAction.CancelScan),
            Display(ShortcutGroup.List, "Remove the selected entries", "Delete"),

            // App
            Command(ShortcutGroup.App, "Show this shortcuts list", cmd, shift: false, Key.OemQuestion, "Slash", ShortcutAction.ShowShortcuts),
        };

        if (hasSettings)
        {
            // Settings is Windows-only; insert it before the always-present shortcuts row so the
            // App section reads Settings, then Shortcuts.
            items.Insert(items.Count - 1, Command(
                ShortcutGroup.App, "Open Settings", cmd, shift: false, Key.OemComma, ",", ShortcutAction.OpenSettings));
        }

        return items;
    }

    /// <summary>
    /// Builds a command accelerator from one definition so the label and the gesture cannot diverge.
    /// The label is always the universal <c>Cmd/Ctrl+…</c>; only the gesture's modifier is platform-resolved.
    /// </summary>
    private static ShortcutItem Command(
        ShortcutGroup group, string description, KeyModifiers cmd, bool shift, Key key, string keyName, ShortcutAction action)
    {
        var label = "Cmd/Ctrl+" + (shift ? "Shift+" : "") + keyName;
        var modifiers = cmd | (shift ? KeyModifiers.Shift : KeyModifiers.None);
        return new ShortcutItem(group, description, label, new KeyGesture(key, modifiers), action);
    }

    private static ShortcutItem Display(ShortcutGroup group, string description, string label, bool asKeycap = true) =>
        new(group, description, label, ShowAsKeycap: asKeycap);
}
