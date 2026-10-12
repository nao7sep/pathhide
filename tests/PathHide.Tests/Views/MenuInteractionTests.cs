using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PathHide.Tests.I18n;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class MenuInteractionTests : WindowTest
{
    private (Window Window, Button Button, MenuFlyout Flyout, MenuItem[] Items, List<int> Clicks) Menu()
    {
        var window = Show(PopulatedMainWindow.Empty());
        var button = window.FindControl<Button>("MenuButton")!;
        var flyout = (MenuFlyout)button.Flyout!;
        var clicks = new List<int>();
        var items = flyout.Items.OfType<MenuItem>().ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            var clicked = index;
            items[index].Click += (_, _) => clicks.Add(clicked);
        }
        return (window, button, flyout, items, clicks);
    }

    private static Point Center(Control control, TopLevel root) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;

    private static void Key(TopLevel root, Avalonia.Input.Key key)
    {
        root.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        root.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opening_initializes_the_target_and_the_first_arrow_moves_from_it(bool keyboard)
    {
        var menu = Menu();
        try
        {
            if (keyboard)
            {
                menu.Button.Focus(NavigationMethod.Tab);
                Key(menu.Window, Avalonia.Input.Key.Enter);
            }
            else
            {
                var point = Center(menu.Button, menu.Window);
                menu.Window.MouseMove(point);
                menu.Window.MouseDown(point, MouseButton.Left);
                menu.Window.MouseUp(point, MouseButton.Left);
            }
            Dispatcher.UIThread.RunJobs();
            var presenter = menu.Items[0].FindAncestorOfType<MenuFlyoutPresenter>()!;
            Assert.NotNull(presenter);
            Assert.Equal(0, presenter.SelectedIndex);
            var highlight = menu.Items[0].GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "PART_LayoutRoot");
            Assert.Equal(Application.Current!.FindResource(menu.Window.ActualThemeVariant, "RowSelectedBrush"), highlight.Background);
            var root = TopLevel.GetTopLevel(presenter)!;
            Key(root, Avalonia.Input.Key.Down);
            Assert.Equal(1, presenter.SelectedIndex);
            Assert.Empty(menu.Clicks);
        }
        finally { menu.Flyout.Hide(); }
    }

    [AvaloniaFact]
    public void Hovering_an_earlier_command_keeps_the_keyboard_target_and_Enter_acts_on_it()
    {
        var menu = Menu();
        try
        {
            menu.Button.Focus(NavigationMethod.Tab);
            Key(menu.Window, Avalonia.Input.Key.Enter);
            var presenter = menu.Items[0].FindAncestorOfType<MenuFlyoutPresenter>()!;
            var root = TopLevel.GetTopLevel(presenter)!;
            Key(root, Avalonia.Input.Key.Down);
            Key(root, Avalonia.Input.Key.Down);
            Assert.Equal(2, presenter.SelectedIndex);
            root.MouseMove(Center(menu.Items[0], root));
            Assert.True(menu.Items[0].IsPointerOver);
            Assert.Equal(2, presenter.SelectedIndex);
            Assert.True(menu.Items[2].IsFocused);
            var hovered = menu.Items[0].GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "PART_LayoutRoot");
            var active = menu.Items[2].GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "PART_LayoutRoot");
            Assert.NotEqual(((ISolidColorBrush)hovered.Background!).Color, ((ISolidColorBrush)active.Background!).Color);
            Key(root, Avalonia.Input.Key.Down);
            Assert.Equal(3, presenter.SelectedIndex);
            Key(root, Avalonia.Input.Key.Enter);
            Assert.Equal([3], menu.Clicks);
        }
        finally
        {
            menu.Flyout.Hide();
            foreach (var owned in menu.Window.OwnedWindows.ToArray())
                Track(owned);
        }
    }

    [AvaloniaFact]
    public void Clicking_acts_on_the_clicked_command_while_another_command_has_keyboard_focus()
    {
        var menu = Menu();
        try
        {
            menu.Flyout.ShowAt(menu.Button);
            Dispatcher.UIThread.RunJobs();
            var root = TopLevel.GetTopLevel(menu.Items[0])!;
            menu.Items[2].Focus(NavigationMethod.Directional);
            var point = Center(menu.Items[0], root);
            root.MouseMove(point);
            root.MouseDown(point, MouseButton.Left);
            root.MouseUp(point, MouseButton.Left);
            Assert.Equal([0], menu.Clicks);
        }
        finally
        {
            menu.Flyout.Hide();
            foreach (var owned in menu.Window.OwnedWindows.ToArray())
                Track(owned);
        }
    }
}
