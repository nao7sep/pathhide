using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using PathHide.Services;
using static PathHide.Views.ObjC;

namespace PathHide.Views;

/// <summary>
/// The macOS menu bar (app-chrome conventions, macOS menu bar): one bar for the whole app, set once at
/// startup and never swapped, as in any Mac app. Avalonia's NativeMenu swaps the whole bar whenever a
/// window gains or loses focus, and when a dialog closes the swaps can land out of order and leave the
/// bar without its Edit and Window menus, so it is turned off (<see cref="PlatformOptions"/>) and this
/// builds the bar through AppKit.
///
/// The Edit and Window items send AppKit's standard actions to whatever has focus, as a native app's
/// do: they work in the native Open and Save panels too, macOS adds Emoji &amp; Symbols, Start
/// Dictation, and AutoFill to the Edit menu, and it lists the open windows in the Window menu.
/// Avalonia's view does not answer the edit actions, so this teaches it to (see <see cref="Route"/>).
/// </summary>
internal static class MacMenuBar
{
    /// <summary>
    /// A menu item: its title, the action it sends, and its key equivalent. AppKit matches a key
    /// equivalent against the characters the key types with Shift applied, so a shifted shortcut is
    /// written in upper case (Redo is "Z" with Command, shown as Shift-Command-Z), as in Apple's menus.
    /// </summary>
    internal sealed record Item(string Title, string Action, string Key = "", Modifiers Modifiers = Modifiers.Command);

    /// <summary>AppKit's modifier flags, as key equivalents and key events carry them.</summary>
    [Flags]
    internal enum Modifiers : ulong
    {
        Shift = 1 << 17,
        Option = 1 << 19,
        Command = 1 << 20,
    }

    // The actions PathHide answers itself; every other action is AppKit's own. Quit shuts down
    // through the lifetime, as Avalonia's own Quit item did, so the app's shutdown path runs.
    internal const string AboutAction = "showAbout:";
    internal const string SettingsAction = "showSettings:";
    internal const string QuitAction = "quit:";
    internal static readonly string[] AppActions = [AboutAction, SettingsAction, QuitAction];

    // Stands for the Services submenu, which macOS fills; it sends no action.
    internal const string ServicesSubmenu = "(services)";

    internal static readonly string[] EditActions = ["undo:", "redo:", "cut:", "copy:", "paste:", "selectAll:"];

    /// <summary>The bar's menus in order; a null item is a separator.</summary>
    internal static IReadOnlyList<(string Title, Item?[] Items)> Layout(string appName) =>
    [
        (appName,
        [
            new($"About {appName}", AboutAction),
            null,
            new("Settings…", SettingsAction, ","),
            null,
            new("Services", ServicesSubmenu),
            null,
            new($"Hide {appName}", "hide:", "h"),
            new("Hide Others", "hideOtherApplications:", "h", Modifiers.Command | Modifiers.Option),
            new("Show All", "unhideAllApplications:"),
            null,
            new($"Quit {appName}", QuitAction, "q"),
        ]),
        ("Edit",
        [
            new("Undo", "undo:", "z"),
            new("Redo", "redo:", "Z"),
            null,
            new("Cut", "cut:", "x"),
            new("Copy", "copy:", "c"),
            new("Paste", "paste:", "v"),
            new("Select All", "selectAll:", "a"),
        ]),
        ("Window",
        [
            new("Minimize", "performMiniaturize:", "m"),
            new("Zoom", "performZoom:"),
            null,
            new("Bring All to Front", "arrangeInFront:"),
        ]),
    ];

    /// <summary>
    /// Avalonia's own macOS menus, turned off, which the app passes to its AppBuilder: NativeMenu would
    /// otherwise swap in a bar of its own whenever a window gains or loses focus, replacing this one.
    /// </summary>
    internal static MacOSPlatformOptions PlatformOptions() => new() { DisableNativeMenus = true };

    /// <summary>Where an Edit item's action goes.</summary>
    internal enum EditRoute
    {
        /// <summary>Nothing can take it; the item shows disabled.</summary>
        None,

        /// <summary>The item's own shortcut: back to Avalonia as the key it was.</summary>
        Forward,

        /// <summary>A click: the focused text field's own command.</summary>
        EditField,
    }

    /// <summary>
    /// Routes an Edit item. Its shortcut goes back to Avalonia as the key it was, so the focused
    /// control handles it exactly as it would with no menu: a text field edits, a list selects all, a
    /// window binding runs. The item has to accept its shortcut for that, because AppKit swallows the
    /// key of an item that reports itself disabled. A click edits the focused text field, and the item
    /// shows disabled when that field cannot take it.
    /// </summary>
    internal static EditRoute Route(string action, TextBox? field, bool shortcut) =>
        shortcut ? EditRoute.Forward
        : field is not null && EditCommand(field, action).Available ? EditRoute.EditField
        : EditRoute.None;

    /// <summary>What an Edit action does to <paramref name="field"/>, and whether it can now.</summary>
    internal static (bool Available, Action Run) EditCommand(TextBox field, string action) => action switch
    {
        "undo:" => (field.CanUndo, field.Undo),
        "redo:" => (field.CanRedo, field.Redo),
        "cut:" => (field.CanCut, field.Cut),
        "copy:" => (field.CanCopy, field.Copy),
        "paste:" => (field.CanPaste, field.Paste),
        "selectAll:" => (!string.IsNullOrEmpty(field.Text), field.SelectAll),
        _ => (false, static () => { }),
    };

    // What AppKit and Avalonia report to the Edit items: the focused text field of the window that owns
    // a native view, and the Command key-down being dispatched (zero for anything else). AppKit has
    // already matched that key-down to the item it validates or sends, so it is the item's own
    // shortcut. Tests replace them.
    internal static Func<IntPtr, TextBox?> FocusedFieldOf = FocusedFieldOfWindowWithView;
    internal static Func<IntPtr> CurrentShortcut = ReadCurrentShortcut;

    /// <summary>Whether <see cref="Install"/> has set the bar in this process.</summary>
    internal static bool Installed { get; private set; }

    private static Action s_showAbout = () => { };
    private static Action s_showSettings = () => { };
    private static Func<bool> s_canShowAppDialogs = () => false;

    // AppKit calls back through these; the fields keep the delegates alive for the process's life.
    private static readonly ActionImp s_appAction = OnAppAction;
    private static readonly ValidateImp s_validateAppItem = ValidateAppItem;
    private static readonly ActionImp s_editAction = OnEditAction;
    private static readonly ValidateImp s_validateEditItem = ValidateEditItem;

    /// <summary>
    /// Sets the menu bar once, after Avalonia has started AppKit, in the desktop app only. About and
    /// Settings are enabled only while <paramref name="canShowAppDialogs"/> holds, which the app ties to
    /// its main window being in front, as a Mac app disables them while a dialog is up. Does nothing
    /// off macOS.
    /// </summary>
    public static void Install(string appName, Action showAbout, Action showSettings, Func<bool> canShowAppDialogs)
    {
        if (!OperatingSystem.IsMacOS() || Installed)
            return;
        Installed = true;
        ConfigureAppActions(showAbout, showSettings, canShowAppDialogs);

        try
        {
            var bar = BuildBar(appName, CreateAppActionTarget(appName + "MenuActions"));

            // Avalonia's view is the first responder in every Avalonia window.
            var view = Class("AvnView");
            if (view == IntPtr.Zero)
                Log.Warn("ui: Avalonia's macOS view class was not found; the Edit menu cannot reach its text fields");
            else
                AddEditActions(view);

            var app = Send(Class("NSApplication"), "sharedApplication");
            Send(app, "setServicesMenu:", bar.Services);
            Send(app, "setWindowsMenu:", bar.Window);
            Send(app, "setMainMenu:", bar.Bar);
        }
        catch (Exception ex)
        {
            // Without the bar the app still runs; macOS shows its bare app menu.
            Log.Error("ui: the macOS menu bar could not be set", ex);
        }
    }

    internal static void ConfigureAppActions(Action showAbout, Action showSettings, Func<bool> canShowAppDialogs)
    {
        s_showAbout = showAbout;
        s_showSettings = showSettings;
        s_canShowAppDialogs = canShowAppDialogs;
    }

    /// <summary>The native bar, and the Services and Window menus AppKit is told about.</summary>
    internal readonly record struct NativeBar(IntPtr Bar, IntPtr Services, IntPtr Window);

    /// <summary>Builds the native bar from <see cref="Layout"/>, without setting it.</summary>
    internal static NativeBar BuildBar(string appName, IntPtr appActionTarget)
    {
        // Every message to a class that is not loaded returns nothing, so without AppKit this would
        // build an empty bar and report no error.
        if (Class("NSMenu") == IntPtr.Zero)
            throw new InvalidOperationException("AppKit is not loaded.");

        var bar = NewMenu("");
        var services = IntPtr.Zero;
        var window = IntPtr.Zero;
        foreach (var (title, items) in Layout(appName))
        {
            var menu = NewMenu(title);
            foreach (var item in items)
            {
                IntPtr native;
                if (item is null)
                {
                    native = Send(Class("NSMenuItem"), "separatorItem");
                }
                else if (item.Action == ServicesSubmenu)
                {
                    services = NewMenu(item.Title);
                    native = NewSubmenuItem(item.Title, services);
                }
                else
                {
                    native = NewItem(item, appActionTarget);
                }

                Send(menu, "addItem:", native);
            }

            if (title == "Window")
                window = menu;
            Send(bar, "addItem:", NewSubmenuItem(title, menu));
        }

        return new NativeBar(bar, services, window);
    }

    private static IntPtr NewMenu(string title) =>
        Send(Send(Class("NSMenu"), "alloc"), "initWithTitle:", NSString(title));

    private static IntPtr NewSubmenuItem(string title, IntPtr menu)
    {
        var native = Send(Send(Class("NSMenuItem"), "alloc"), "initWithTitle:action:keyEquivalent:",
            NSString(title), IntPtr.Zero, NSString(""));
        Send(native, "setSubmenu:", menu);
        return native;
    }

    internal static IntPtr NewItem(Item item, IntPtr appActionTarget)
    {
        var native = Send(Send(Class("NSMenuItem"), "alloc"), "initWithTitle:action:keyEquivalent:",
            NSString(item.Title), Sel(item.Action), NSString(item.Key));
        SendUInt(native, "setKeyEquivalentModifierMask:", (ulong)item.Modifiers);
        // Without a target an item's action goes to whatever has focus and on up to the app; the app's
        // own actions go to their object.
        if (AppActions.Contains(item.Action))
            Send(native, "setTarget:", appActionTarget);
        return native;
    }

    /// <summary>An object of a new class <paramref name="className"/> that answers About, Settings, and Quit.</summary>
    internal static IntPtr CreateAppActionTarget(string className)
    {
        var type = DefineClass(className, "NSObject", type =>
        {
            foreach (var action in AppActions)
                AddMethod(type, action, s_appAction, ActionMethodTypes);
            AddMethod(type, "validateMenuItem:", s_validateAppItem, BoolMethodTypes);
        });
        return Send(Send(type, "alloc"), "init");
    }

    /// <summary>
    /// Teaches a view class the Edit actions. On Avalonia's view they sit ahead of NSWindow, which
    /// would otherwise take Undo and Redo, while a native panel's own text field still answers them in
    /// that panel. A method the class already defines is kept, so a later Avalonia that answers these
    /// actions itself keeps its own.
    /// </summary>
    internal static void AddEditActions(IntPtr viewClass)
    {
        foreach (var action in EditActions)
        {
            if (!AddMethod(viewClass, action, s_editAction, ActionMethodTypes))
                Log.Info("ui: the macOS view already answers an Edit action", new { action });
        }

        if (!AddMethod(viewClass, "validateMenuItem:", s_validateEditItem, BoolMethodTypes))
            Log.Info("ui: the macOS view already validates menu items");
    }

    private static void OnAppAction(IntPtr self, IntPtr selector, IntPtr sender)
    {
        try
        {
            switch (SelectorName(selector))
            {
                case AboutAction: s_showAbout(); break;
                case SettingsAction: s_showSettings(); break;
                case QuitAction:
                    (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.TryShutdown();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("ui: a macOS menu item failed", ex);
        }
    }

    private static byte ValidateAppItem(IntPtr self, IntPtr selector, IntPtr menuItem)
    {
        try
        {
            var action = SelectorName(Send(menuItem, "action"));
            return action is AboutAction or SettingsAction && !s_canShowAppDialogs() ? (byte)0 : (byte)1;
        }
        catch (Exception ex)
        {
            Log.Error("ui: a macOS menu item could not be validated", ex);
            return 0;
        }
    }

    private static void OnEditAction(IntPtr view, IntPtr selector, IntPtr sender)
    {
        try
        {
            var action = SelectorName(selector);
            var shortcut = ShortcutSentBy(sender);
            var field = shortcut == IntPtr.Zero ? FocusedFieldOf(view) : null;
            switch (Route(action, field, shortcut != IntPtr.Zero))
            {
                case EditRoute.Forward:
                    Send(view, "keyDown:", shortcut);
                    break;
                case EditRoute.EditField:
                    EditCommand(field!, action).Run();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("ui: a macOS Edit menu item failed", ex);
        }
    }

    private static byte ValidateEditItem(IntPtr view, IntPtr selector, IntPtr menuItem)
    {
        try
        {
            var action = SelectorName(Send(menuItem, "action"));
            if (!EditActions.Contains(action))
                return 1;
            var shortcut = ShortcutSentBy(menuItem) != IntPtr.Zero;
            return Route(action, shortcut ? null : FocusedFieldOf(view), shortcut) == EditRoute.None ? (byte)0 : (byte)1;
        }
        catch (Exception ex)
        {
            Log.Error("ui: a macOS Edit menu item could not be validated", ex);
            return 0;
        }
    }

    // The shortcut being dispatched, when a menu item sends or validates the action; an action sent by
    // anything but a menu item is never one.
    private static IntPtr ShortcutSentBy(IntPtr sender)
    {
        var shortcut = CurrentShortcut();
        return shortcut != IntPtr.Zero && SendForBool(sender, "isKindOfClass:", Class("NSMenuItem")) ? shortcut : IntPtr.Zero;
    }

    private static IntPtr ReadCurrentShortcut()
    {
        const ulong keyDown = 10; // NSEventTypeKeyDown

        var current = Send(Send(Class("NSApplication"), "sharedApplication"), "currentEvent");
        return current != IntPtr.Zero
            && SendForUInt(current, "type") == keyDown
            && (SendForUInt(current, "modifierFlags") & (ulong)Modifiers.Command) != 0
            ? current
            : IntPtr.Zero;
    }

    private static TextBox? FocusedFieldOfWindowWithView(IntPtr view)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;
        var window = desktop.Windows.FirstOrDefault(window =>
            window.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle handle && handle.NSView == view);
        return window?.FocusManager?.GetFocusedElement() as TextBox;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ActionImp(IntPtr self, IntPtr selector, IntPtr sender);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ValidateImp(IntPtr self, IntPtr selector, IntPtr menuItem);
}
