using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PathHide.Views;
using Xunit;
using static PathHide.Views.MacMenuBar;

namespace PathHide.Tests.Views;

/// <summary>
/// The macOS menu bar is one bar for the whole app, built through AppKit once. These check its layout,
/// how its Edit items route a shortcut and a click, and, on a Mac, the native bar itself: that it
/// matches the layout, that the keys that type each shortcut reach its item, that PathHide's own
/// window shortcuts get past it, and that the Edit actions reach a text field through AppKit.
/// </summary>
public sealed class MacMenuBarTests
{
    private const Modifiers Cmd = Modifiers.Command;

    // ── The layout ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_bar_is_the_app_menu_then_Edit_then_Window()
    {
        Assert.Equal(new[] { "PathHide", "Edit", "Window" }, Layout("PathHide").Select(menu => menu.Title));
    }

    [Fact]
    public void The_app_menu_names_PathHide_and_carries_About_Settings_Services_Hide_and_Quit()
    {
        Assert.Equal(
            new[]
            {
                "About PathHide", "—", "Settings…", "—", "Services", "—",
                "Hide PathHide", "Hide Others", "Show All", "—", "Quit PathHide",
            },
            Titles(Menu("PathHide")));
        Assert.Equal((",", Cmd), KeyOf(Menu("PathHide"), "Settings…"));
        Assert.Equal(("q", Cmd), KeyOf(Menu("PathHide"), "Quit PathHide"));
    }

    [Fact]
    public void Edit_and_Window_items_send_AppKits_standard_actions_with_the_standard_keys()
    {
        Assert.Equal(new[] { "Undo", "Redo", "—", "Cut", "Copy", "Paste", "Select All" }, Titles(Menu("Edit")));
        Assert.Equal(EditActions, Menu("Edit").OfType<Item>().Select(item => item.Action));
        Assert.Equal(
            new[] { ("z", Cmd), ("Z", Cmd), ("x", Cmd), ("c", Cmd), ("v", Cmd), ("a", Cmd) },
            Menu("Edit").OfType<Item>().Select(item => (item.Key, item.Modifiers)));

        Assert.Equal(new[] { "Minimize", "Zoom", "—", "Bring All to Front" }, Titles(Menu("Window")));
        Assert.Equal(
            new[] { "performMiniaturize:", "performZoom:", "arrangeInFront:" },
            Menu("Window").OfType<Item>().Select(item => item.Action));
        Assert.Equal(("m", Cmd), KeyOf(Menu("Window"), "Minimize"));
    }

    [Fact]
    public void Only_About_Settings_and_Quit_are_answered_by_PathHide()
    {
        var actions = Layout("PathHide").SelectMany(menu => menu.Items).OfType<Item>()
            .Where(item => item.Action != ServicesSubmenu)
            .Select(item => item.Action)
            .ToList();
        Assert.Equal(new[] { AboutAction, SettingsAction, QuitAction }, actions.Where(AppActions.Contains));
        Assert.Equal(
            new[] { "hide:", "hideOtherApplications:", "unhideAllApplications:" },
            Menu("PathHide").OfType<Item>().Select(item => item.Action)
                .Where(action => !AppActions.Contains(action) && action != ServicesSubmenu));
    }

    [Fact]
    public void No_two_items_share_a_shortcut()
    {
        var shortcuts = Layout("PathHide").SelectMany(menu => menu.Items).OfType<Item>()
            .Where(item => item.Key.Length > 0)
            .Select(item => Typed(item))
            .ToList();
        Assert.Equal(shortcuts.Count, shortcuts.Distinct().Count());
    }

    // ── Routing ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_shortcut_always_goes_back_to_Avalonia_even_with_no_text_field_focused()
    {
        // AppKit swallows the key of an item that reports itself disabled, so a shortcut is always
        // accepted and forwarded: a list or a window binding gets it as it would with no menu.
        foreach (var action in EditActions)
            Assert.Equal(EditRoute.Forward, Route(action, field: null, shortcut: true));
    }

    [Fact]
    public void A_click_with_no_text_field_focused_does_nothing()
    {
        foreach (var action in EditActions)
            Assert.Equal(EditRoute.None, Route(action, field: null, shortcut: false));
    }

    [AvaloniaFact]
    public void A_click_edits_the_focused_text_field_only_when_the_field_can_take_it()
    {
        var field = new TextBox { Text = "scripts" };
        var window = new Window { Content = field };
        try
        {
            window.Show();
            field.Focus();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(EditRoute.None, Route("copy:", field, shortcut: false));
            Assert.Equal(EditRoute.None, Route("cut:", field, shortcut: false));
            Assert.Equal(EditRoute.EditField, Route("paste:", field, shortcut: false));
            Assert.Equal(EditRoute.EditField, Route("selectAll:", field, shortcut: false));

            EditCommand(field, "selectAll:").Run();
            Assert.Equal("scripts", field.SelectedText);
            Assert.Equal(EditRoute.EditField, Route("copy:", field, shortcut: false));
            Assert.Equal(EditRoute.EditField, Route("cut:", field, shortcut: false));

            field.IsReadOnly = true;
            Assert.Equal(EditRoute.EditField, Route("copy:", field, shortcut: false));
            Assert.Equal(EditRoute.None, Route("cut:", field, shortcut: false));
            Assert.Equal(EditRoute.None, Route("paste:", field, shortcut: false));

            field.IsReadOnly = false;
            field.Text = "";
            Assert.Equal(EditRoute.None, Route("selectAll:", field, shortcut: false));
        }
        finally
        {
            window.Close();
        }
    }

    // ── The app around it ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_bar_turns_off_Avalonias_own_macOS_menus() =>
        Assert.True(PlatformOptions().DisableNativeMenus);

    [Fact]
    public void No_window_or_the_app_declares_an_Avalonia_NativeMenu()
    {
        // With Avalonia's menus off, one would be silently ignored; the bar is MacMenuBar's alone.
        var declaration = new Regex(@"<NativeMenu|NativeMenu\.(Set)?Menu|new NativeMenu");
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "PathHide"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".axaml", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => declaration.IsMatch(File.ReadAllText(path)))
            .ToList();
        Assert.Empty(offenders);
    }

    [AvaloniaFact]
    public void The_headless_test_app_does_not_install_the_bar() =>
        // The bar is the desktop app's; the headless lifetime is not a desktop one.
        Assert.False(Installed);

    // ── The native bar (macOS) ──────────────────────────────────────────────────────────────────

    [MacOnlyFact]
    public void The_native_bar_matches_the_layout()
    {
        LoadAppKit();
        var target = CreateAppActionTarget(UniqueClassName("Target"));
        var bar = BuildBar("PathHide", target);
        var layout = Layout("PathHide");

        Assert.Equal(layout.Count, Count(bar.Bar));
        for (var m = 0; m < layout.Count; m++)
        {
            var menu = ObjC.Send(ItemAt(bar.Bar, m), "submenu");
            Assert.Equal(layout[m].Title, ObjC.String(ObjC.Send(menu, "title")));
            Assert.Equal(layout[m].Items.Length, Count(menu));
            if (layout[m].Title == "Window")
                Assert.Equal(bar.Window, menu);

            for (var i = 0; i < layout[m].Items.Length; i++)
            {
                var native = ItemAt(menu, i);
                if (layout[m].Items[i] is not { } item)
                {
                    Assert.True(IsSeparator(native));
                    continue;
                }

                Assert.Equal(item.Title, ObjC.String(ObjC.Send(native, "title")));
                if (item.Action == ServicesSubmenu)
                {
                    Assert.Equal(bar.Services, ObjC.Send(native, "submenu"));
                    continue;
                }

                Assert.Equal(item.Action, ObjC.SelectorName(ObjC.Send(native, "action")));
                Assert.Equal(item.Key, ObjC.String(ObjC.Send(native, "keyEquivalent")));
                Assert.Equal((ulong)item.Modifiers, ObjC.SendForUInt(native, "keyEquivalentModifierMask"));
                var expectedTarget = AppActions.Contains(item.Action) ? target : IntPtr.Zero;
                Assert.Equal(expectedTarget, ObjC.Send(native, "target"));
            }
        }
    }

    [MacOnlyFact]
    public void The_keys_that_type_each_shortcut_reach_the_native_bar()
    {
        LoadAppKit();
        var bar = BuildBar("PathHide", CreateAppActionTarget(UniqueClassName("Target")));
        foreach (var item in Layout("PathHide").SelectMany(menu => menu.Items).OfType<Item>().Where(item => item.Key.Length > 0))
        {
            var (characters, modifiers) = Typed(item);
            Assert.True(PerformsKeyEquivalent(bar.Bar, characters, modifiers), $"{item.Title} did not take its shortcut");
        }
    }

    [AvaloniaFact]
    public void PathHides_own_window_shortcuts_get_past_the_native_bar_except_Settings()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Runs on macOS only.");
        LoadAppKit();
        var window = new Window();
        window.Show();
        var bar = BuildBar("PathHide", CreateAppActionTarget(UniqueClassName("Target")));

        // The characters each catalog key types; a new shortcut key needs its entry here.
        var typed = new Dictionary<Key, string>
        {
            [Key.O] = "o", [Key.H] = "h", [Key.S] = "s", [Key.R] = "r", [Key.OemComma] = ",", [Key.OemQuestion] = "/",
        };
        var command = ShortcutCatalog.CommandModifier(window);
        foreach (var shortcut in ShortcutCatalog.Build(window).Where(item => item.Gesture is not null))
        {
            // A key without Command, such as Escape, cannot match any item in the bar.
            var gesture = shortcut.Gesture!;
            if (!gesture.KeyModifiers.HasFlag(command))
                continue;
            Assert.True(typed.ContainsKey(gesture.Key), $"No typed character for {gesture.Key}");
            var shift = gesture.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var characters = shift ? typed[gesture.Key].ToUpperInvariant() : typed[gesture.Key];
            var taken = PerformsKeyEquivalent(bar.Bar, characters, shift ? Cmd | Modifiers.Shift : Cmd);

            // Settings is the one shortcut the bar owns; its item runs the same command.
            Assert.Equal(shortcut.Action == ShortcutAction.OpenSettings, taken);
        }

        window.Close();
    }

    [MacOnlyFact]
    public void Avalonias_macOS_view_is_still_the_class_the_Edit_actions_are_taught()
    {
        // The bar teaches the Edit actions to Avalonia's view class, found by name. An Avalonia upgrade
        // that renames it would leave every Edit item disabled in its windows, and AppKit swallows a
        // disabled item's shortcut, so Command-C, V, X, A, and Z would stop working in text fields. One
        // that answers these actions itself keeps its own, which needs a look before upgrading.
        NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "runtimes", "osx", "native", "libAvaloniaNative.dylib"));
        var view = ObjC.Class("AvnView");
        Assert.NotEqual(IntPtr.Zero, view);
        Assert.True(InstancesRespondTo(view, "keyDown:"));
        foreach (var action in EditActions.Append("validateMenuItem:"))
            Assert.False(InstancesRespondTo(view, action), $"Avalonia's view now answers {action} itself");
    }

    [MacOnlyFact]
    public void The_app_target_answers_About_and_Settings_and_disables_them_when_told()
    {
        LoadAppKit();
        var target = CreateAppActionTarget(UniqueClassName("Target"));
        var about = 0;
        var settings = 0;
        var canShow = true;
        ConfigureAppActions(() => about++, () => settings++, () => canShow);
        try
        {
            ObjC.Send(target, AboutAction, IntPtr.Zero);
            ObjC.Send(target, SettingsAction, IntPtr.Zero);
            Assert.Equal((1, 1), (about, settings));

            var aboutItem = NewItem(new Item("About PathHide", AboutAction), target);
            var quitItem = NewItem(new Item("Quit PathHide", QuitAction, "q"), target);
            Assert.True(Validates(target, aboutItem));
            canShow = false;
            Assert.False(Validates(target, aboutItem));
            Assert.True(Validates(target, quitItem));
        }
        finally
        {
            ConfigureAppActions(() => { }, () => { }, () => false);
        }
    }

    [AvaloniaFact]
    public void The_Edit_actions_reach_the_focused_text_field_through_AppKit()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Runs on macOS only.");
        LoadAppKit();

        // A stand-in for Avalonia's view, taught the Edit actions the same way, whose keyDown: records
        // the event it is handed.
        var view = ObjC.Send(ObjC.Send(ObjC.DefineClass(UniqueClassName("View"), "NSObject", type =>
        {
            AddEditActions(type);
            ObjC.AddMethod(type, "keyDown:", RecordKeyDown, ObjC.ActionMethodTypes);
        }), "alloc"), "init");
        var items = Menu("Edit").OfType<Item>().ToDictionary(item => item.Action, item => NewItem(item, IntPtr.Zero));

        var field = new TextBox { Text = "scripts" };
        var window = new Window { Content = field };
        var focusedFieldOf = FocusedFieldOf;
        var currentShortcut = CurrentShortcut;
        try
        {
            window.Show();
            field.Focus();
            Dispatcher.UIThread.RunJobs();
            s_forwarded.Clear();

            foreach (var action in EditActions)
                Assert.True(ObjC.SendForBool(view, "respondsToSelector:", ObjC.Sel(action)), action);

            // A click: the items follow the field, and act on it.
            FocusedFieldOf = _ => field;
            CurrentShortcut = () => IntPtr.Zero;
            Assert.False(Validates(view, items["copy:"]));
            Assert.True(Validates(view, items["selectAll:"]));
            ObjC.Send(view, "selectAll:", items["selectAll:"]);
            Assert.Equal("scripts", field.SelectedText);
            Assert.True(Validates(view, items["copy:"]));

            // Its shortcut: accepted even with no field, and handed back as the key it was.
            FocusedFieldOf = _ => null;
            var shortcut = new IntPtr(0x5eed);
            CurrentShortcut = () => shortcut;
            Assert.True(Validates(view, items["copy:"]));
            ObjC.Send(view, "copy:", items["copy:"]);
            Assert.Equal(new[] { shortcut }, s_forwarded);

            // An action no menu item sent is not a shortcut, even while one is being dispatched.
            ObjC.Send(view, "copy:", IntPtr.Zero);
            Assert.Single(s_forwarded);
        }
        finally
        {
            FocusedFieldOf = focusedFieldOf;
            CurrentShortcut = currentShortcut;
            window.Close();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static readonly List<IntPtr> s_forwarded = [];

    private delegate void KeyDownImp(IntPtr self, IntPtr selector, IntPtr keyEvent);

    private static readonly KeyDownImp RecordKeyDown = (_, _, keyEvent) => s_forwarded.Add(keyEvent);

    private static Item?[] Menu(string title) => Layout("PathHide").Single(menu => menu.Title == title).Items;

    private static IEnumerable<string> Titles(Item?[] items) => items.Select(item => item?.Title ?? "—");

    private static (string, Modifiers) KeyOf(Item?[] items, string title)
    {
        var item = items.OfType<Item>().Single(item => item.Title == title);
        return (item.Key, item.Modifiers);
    }

    // What the keyboard sends for an item's shortcut: a letter typed with Shift held arrives in upper
    // case, which is what AppKit matches against.
    private static (string Characters, Modifiers Modifiers) Typed(Item item)
    {
        var shift = item.Key.Any(char.IsUpper) || item.Modifiers.HasFlag(Modifiers.Shift);
        return shift ? (item.Key.ToUpperInvariant(), item.Modifiers | Modifiers.Shift) : (item.Key, item.Modifiers);
    }

    // The app gets AppKit through Avalonia's native backend; the headless test process loads it here.
    // Without it every message to an AppKit class returns nothing and a native test proves nothing.
    private static void LoadAppKit()
    {
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        Assert.NotEqual(IntPtr.Zero, ObjC.Class("NSMenu"));
    }

    private static string UniqueClassName(string role) => $"PathHideMenuBarTest{role}{Guid.NewGuid():N}";

    private static long Count(IntPtr menu) => (long)ObjC.SendForUInt(menu, "numberOfItems");

    private static IntPtr ItemAt(IntPtr menu, int index) => ObjC.Send(menu, "itemAtIndex:", new IntPtr(index));

    private static bool InstancesRespondTo(IntPtr type, string selector) =>
        ObjC.SendForBool(type, "instancesRespondToSelector:", ObjC.Sel(selector));

    private static bool Validates(IntPtr target, IntPtr menuItem) => ObjC.SendForBool(target, "validateMenuItem:", menuItem);

    // Whether the bar takes a key-down, as AppKit's own shortcut matching decides it.
    private static bool PerformsKeyEquivalent(IntPtr bar, string characters, Modifiers modifiers)
    {
        var keyEvent = KeyEvent(ObjC.Class("NSEvent"), ObjC.Sel(
                "keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
            10, default, (ulong)modifiers, 0, 0, IntPtr.Zero, ObjC.NSString(characters), ObjC.NSString(characters), false, 0);
        return ObjC.SendForBool(bar, "performKeyEquivalent:", keyEvent);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public double X;
        public double Y;
    }

    private static bool IsSeparator(IntPtr menuItem) => SendForBool(menuItem, ObjC.Sel("isSeparatorItem")) != 0;

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern byte SendForBool(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr KeyEvent(
        IntPtr type, IntPtr selector, ulong eventType, Point location, ulong modifierFlags, double timestamp,
        nint windowNumber, IntPtr context, IntPtr characters, IntPtr charactersIgnoringModifiers,
        [MarshalAs(UnmanagedType.I1)] bool isARepeat, ushort keyCode);

    private static string RepoRoot([CallerFilePath] string path = "") =>
        // This file: <repo>/tests/PathHide.Tests/Views/MacMenuBarTests.cs
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));
}
