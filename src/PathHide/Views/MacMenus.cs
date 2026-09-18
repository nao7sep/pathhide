using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace PathHide.Views;

/// <summary>
/// The macOS menu bar (app-chrome conventions, macOS menu bar). Avalonia's default app menu reads
/// "About Avalonia" and has no Edit menu, and macOS attaches its text services — Emoji &amp; Symbols,
/// Start Dictation, AutoFill — to the Edit menu, so without one those services are unreachable.
/// NativeMenu is exported only on macOS; on Windows these menus are inert. The app menu (About,
/// Settings) is declared in App.axaml, where Avalonia reads it.
/// </summary>
internal static class MacMenus
{
    /// <summary>
    /// Gives <paramref name="window"/> the Edit menu (and, for a main window, the Window menu).
    /// Every window gets its own, because macOS shows the key window's menus: a dialog without one
    /// would drop the Edit menu, and with it emoji and dictation, while the dialog is in front.
    /// </summary>
    public static void Attach(Window window, bool includeWindowMenu)
    {
        var menu = new NativeMenu { Menu("Edit", EditItems(window)) };
        if (includeWindowMenu)
        {
            menu.Add(Menu("Window", WindowItems(window)));
        }

        NativeMenu.SetMenu(window, menu);
    }

    // Undo through Select All act on the window's focused text field. A field handles these keys
    // itself; the menu items make them reachable by pointer and are the anchor macOS adds its text
    // services to. They act only while their own window is active, so a key pressed in a native
    // Open or Save panel never edits the field behind it.
    private static NativeMenuItem[] EditItems(Window window) =>
    [
        Item("Undo", () => OnFocusedField(window, field => field.Undo()), new KeyGesture(Key.Z, KeyModifiers.Meta)),
        Item("Redo", () => OnFocusedField(window, field => field.Redo()), new KeyGesture(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift)),
        new NativeMenuItemSeparator(),
        Item("Cut", () => OnFocusedField(window, field => field.Cut()), new KeyGesture(Key.X, KeyModifiers.Meta)),
        Item("Copy", () => OnFocusedField(window, field => field.Copy()), new KeyGesture(Key.C, KeyModifiers.Meta)),
        Item("Paste", () => OnFocusedField(window, field => field.Paste()), new KeyGesture(Key.V, KeyModifiers.Meta)),
        Item("Select All", () => OnFocusedField(window, field => field.SelectAll()), new KeyGesture(Key.A, KeyModifiers.Meta)),
    ];

    private static NativeMenuItem[] WindowItems(Window window) =>
    [
        Item("Minimize", () => window.WindowState = WindowState.Minimized, new KeyGesture(Key.M, KeyModifiers.Meta)),
        Item("Zoom", () => window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized),
    ];

    internal static void OnFocusedField(Window window, Action<TextBox> edit)
    {
        if (window.IsActive && window.FocusManager?.GetFocusedElement() is TextBox field)
        {
            edit(field);
        }
    }

    private static NativeMenuItem Menu(string header, IEnumerable<NativeMenuItemBase> items)
    {
        var menu = new NativeMenu();
        foreach (var item in items)
        {
            menu.Add(item);
        }

        return new NativeMenuItem(header) { Menu = menu };
    }

    private static NativeMenuItem Item(string header, Action onClick, KeyGesture? gesture = null)
    {
        var item = new NativeMenuItem(header) { Gesture = gesture };
        item.Click += (_, _) => onClick();
        return item;
    }
}
