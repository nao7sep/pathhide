using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PathHide.Tests.I18n;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class GridInteractionTests : WindowTest
{
    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static DataGridRow CurrentRow(DataGrid grid) =>
        grid.GetVisualDescendants().OfType<DataGridCell>().Single(cell => cell.Classes.Contains(":current"))
            .FindAncestorOfType<DataGridRow>()!;

    private static Rectangle Background(DataGridRow row) =>
        row.GetVisualDescendants().OfType<Rectangle>().Single(rectangle => rectangle.Name == "BackgroundRectangle");

    private static Point Center(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Keyboard_target_has_a_row_mark_beside_selection_and_hover(bool dark)
    {
        var (window, _) = PopulatedMainWindow.Create();
        window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Show(window);
        var grid = window.FindControl<DataGrid>("PathGrid")!;
        var items = grid.CollectionView.Cast<object>().ToArray();
        grid.SelectedIndex = 0;
        grid.Focus(NavigationMethod.Tab);
        Press(window, Key.Down);
        var target = CurrentRow(grid);
        var background = Background(target);
        Assert.True(grid.IsKeyboardFocusWithin);
        Assert.True(background.StrokeThickness > 0 && background.Stroke is not null,
            "The keyboard target has no row-level mark after suppressing its cell focus marks.");
        Assert.Same(items[1], target.DataContext);

        window.MouseMove(Center(target, window));
        Assert.True(target.IsPointerOver);
        Assert.Equal(Application.Current!.FindResource(window.ActualThemeVariant, "RowSelectedHoverBrush"), background.Fill);
        Assert.NotNull(background.Stroke);
        var selectedFill = ((ISolidColorBrush)background.Fill!).Color;

        window.FindControl<Button>("MenuButton")!.Focus(NavigationMethod.Tab);
        Assert.Null(background.Stroke);
        Assert.Equal(selectedFill, ((ISolidColorBrush)background.Fill!).Color);
    }

    [AvaloniaFact]
    public void Hover_preserves_the_current_row_and_range_anchor_and_click_selects_its_own_row()
    {
        var (window, _) = PopulatedMainWindow.Create();
        Show(window);
        var grid = window.FindControl<DataGrid>("PathGrid")!;
        var items = grid.CollectionView.Cast<object>().ToArray();
        grid.SelectedIndex = 0;
        grid.Focus(NavigationMethod.Tab);
        Press(window, Key.Down);
        var first = grid.GetVisualDescendants().OfType<DataGridRow>().Single(row => ReferenceEquals(row.DataContext, items[0]));
        window.MouseMove(Center(first, window));
        Assert.Same(items[1], CurrentRow(grid).DataContext);
        Assert.Same(items[1], grid.SelectedItem);
        Press(window, Key.Down, RawInputModifiers.Shift);
        Assert.Equal(new[] { items[1], items[2] }, grid.SelectedItems.Cast<object>());
        Assert.Same(items[2], CurrentRow(grid).DataContext);

        var point = Center(first, window);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Same(items[0], grid.SelectedItem);
        Assert.Same(items[0], CurrentRow(grid).DataContext);
    }

    [AvaloniaFact]
    public void Deselecting_the_current_row_leaves_its_keyboard_mark_distinct_from_other_selected_rows()
    {
        var (window, _) = PopulatedMainWindow.Create();
        Show(window);
        var grid = window.FindControl<DataGrid>("PathGrid")!;
        var items = grid.CollectionView.Cast<object>().ToArray();
        grid.SelectedIndex = 0;
        grid.Focus(NavigationMethod.Tab);
        Press(window, Key.Down);
        Press(window, Key.Down, RawInputModifiers.Shift);
        var point = Center(CurrentRow(grid), window);
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.Control);
        Assert.Same(items[1], grid.SelectedItem);
        var target = CurrentRow(grid);
        Assert.NotSame(grid.SelectedItem, target.DataContext);
        Assert.NotNull(Background(target).Stroke);
        Assert.Single(grid.GetVisualDescendants().OfType<DataGridRow>(), row => Background(row).Stroke is not null);
    }

    [AvaloniaFact]
    public void Rows_realized_after_scrolling_mark_only_the_current_native_item()
    {
        var (window, _) = PopulatedMainWindow.Create();
        var grid = window.FindControl<DataGrid>("PathGrid")!;
        grid.Height = 100;
        Show(window);
        var items = grid.CollectionView.Cast<object>().ToArray();
        grid.Focus(NavigationMethod.Tab);
        foreach (var index in new[] { items.Length - 1, 0 })
        {
            grid.SelectedIndex = index;
            grid.ScrollIntoView(items[index], null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var target = CurrentRow(grid);
            Assert.Same(items[index], target.DataContext);
            Assert.NotNull(Background(target).Stroke);
            Assert.Single(grid.GetVisualDescendants().OfType<DataGridRow>(), row => Background(row).Stroke is not null);
        }
    }
}
