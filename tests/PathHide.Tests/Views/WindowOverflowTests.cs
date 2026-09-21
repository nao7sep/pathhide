using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class WindowOverflowTests
{
    // The window carries no scroll region of its own: every pane here bounds itself and scrolls its
    // own content, and the layout's floor fits inside any work area this app runs on, so the native
    // minimum already keeps the window above that floor (app-chrome conventions). A viewport added
    // back would be one that can never scroll — and the app-wide scroll rules that used to aim at it
    // reached nothing else, because a type selector does not enter a control template.
    [AvaloniaFact]
    public void The_window_has_no_scroll_region_of_its_own()
    {
        var window = new MainWindow();
        try
        {
            var layout = window.FindControl<Control>("LayoutRoot")!;
            Assert.Empty(layout.GetSelfAndVisualAncestors().OfType<ScrollViewer>());
        }
        finally
        {
            window.Content = null;
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // The floor is what the window can never go below, so it is what the panes are clamped against.
    [AvaloniaFact]
    public void The_layout_keeps_its_derived_floor_and_fills_the_window_above_it()
    {
        var source = new MainWindow();
        var layout = source.FindControl<Control>("LayoutRoot")!;
        source.Content = null;
        source.Close();
        layout.MinWidth = 900;
        layout.MinHeight = 400;

        var host = new Window { Width = 1300, Height = 1000, Content = layout };
        host.Show();
        host.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(host.ClientSize.Width, layout.Bounds.Width, 0);
        Assert.Equal(host.ClientSize.Height, layout.Bounds.Height, 0);
        Assert.Equal(900, layout.MinWidth);
        Assert.Equal(400, layout.MinHeight);
        host.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
