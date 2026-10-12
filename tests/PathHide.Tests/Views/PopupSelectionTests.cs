using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace PathHide.Tests.Views;

// Settings and Records use the native ComboBox with the application styles, without navigation
// handlers. Exercise that same control rather than assigning its internal popup focus in the test.
public sealed class PopupSelectionTests : WindowTest
{
    private static void Press(TopLevel root, Key key)
    {
        root.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        root.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point Center(Control control, TopLevel root) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Popup_starts_at_selection_and_hover_does_not_redirect_the_next_arrow_or_Enter(bool keyboard)
    {
        var box = new ComboBox { ItemsSource = new[] { "Zero", "One", "Two", "Three" }, SelectedIndex = 1, Width = 200 };
        var window = Show(new Window { Width = 400, Height = 300, Content = box });
        if (keyboard)
        {
            box.Focus(NavigationMethod.Tab);
            Press(window, Key.F4);
        }
        else
        {
            var point = Center(box, window);
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsDropDownOpen);
        var popup = box.GetVisualDescendants().OfType<Popup>().Single();
        var root = TopLevel.GetTopLevel(popup.Child!)!;
        var selected = box.ContainerFromIndex(1)!;
        Assert.True(selected.IsFocused);
        Press(root, Key.Down);
        Assert.True(box.ContainerFromIndex(2)!.IsFocused);
        Assert.Equal(1, box.SelectedIndex);

        root.MouseMove(Center(box.ContainerFromIndex(0)!, root));
        Assert.True(box.ContainerFromIndex(0)!.IsPointerOver);
        Assert.True(box.ContainerFromIndex(2)!.IsFocused);
        Assert.Equal(1, box.SelectedIndex);
        Press(root, Key.Down);
        Assert.True(box.ContainerFromIndex(3)!.IsFocused);
        Press(root, Key.Enter);
        Assert.Equal(3, box.SelectedIndex);
        Assert.False(box.IsDropDownOpen);
    }

    // Records' launch filter gains launches while it may be open; Enter still picks what was highlighted
    // (composite-control conventions).
    [AvaloniaFact]
    public void An_item_inserted_above_the_keyboard_target_while_open_leaves_Enter_on_that_target()
    {
        var items = new System.Collections.ObjectModel.ObservableCollection<string> { "All", "Newest", "Older" };
        var box = new ComboBox { ItemsSource = items, SelectedIndex = 0, Width = 200 };
        var window = Show(new Window { Width = 400, Height = 300, Content = box });
        box.Focus(NavigationMethod.Tab);
        Press(window, Key.F4);
        var popup = box.GetVisualDescendants().OfType<Popup>().Single();
        var root = TopLevel.GetTopLevel(popup.Child!)!;
        Press(root, Key.Down);
        Press(root, Key.Down);
        Assert.Same("Older", box.ContainerFromIndex(2)!.DataContext);
        Assert.True(box.ContainerFromIndex(2)!.IsFocused);

        items.Insert(1, "Inserted");
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.ContainerFromIndex(3)!.IsFocused);
        Assert.Equal(0, box.SelectedIndex);

        Press(root, Key.Enter);
        Assert.Equal("Older", box.SelectedItem);
        Assert.False(box.IsDropDownOpen);
    }

    [AvaloniaFact]
    public void Popup_click_commits_the_clicked_item_instead_of_the_keyboard_target()
    {
        var box = new ComboBox { ItemsSource = new[] { "Zero", "One", "Two" }, SelectedIndex = 1, Width = 200 };
        var window = Show(new Window { Width = 400, Height = 300, Content = box });
        box.Focus(NavigationMethod.Tab);
        Press(window, Key.F4);
        var popup = box.GetVisualDescendants().OfType<Popup>().Single();
        var root = TopLevel.GetTopLevel(popup.Child!)!;
        Press(root, Key.Down);
        var point = Center(box.ContainerFromIndex(0)!, root);
        root.MouseMove(point);
        root.MouseDown(point, MouseButton.Left);
        root.MouseUp(point, MouseButton.Left);
        Assert.Equal(0, box.SelectedIndex);
        Assert.False(box.IsDropDownOpen);
    }
}
