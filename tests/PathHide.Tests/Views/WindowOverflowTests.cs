using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class WindowOverflowTests
{
    [AvaloniaFact]
    public void Main_layout_scrolls_below_its_floor_and_fills_when_space_returns()
    {
        // Exercise the shipped XAML viewport without starting the app's stores or
        // view model. A plain headless window supplies only its available size.
        var source = new MainWindow();
        var viewport = source.FindControl<ScrollViewer>("WindowViewport")!;
        var layout = source.FindControl<Control>("LayoutRoot")!;
        source.Content = null;
        source.Close();
        layout.MinWidth = 900;
        layout.MinHeight = 700;
        var host = new Window { Width = 500, Height = 300, Content = viewport };
        host.Show();
        host.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewport.Extent.Width >= 900);
        Assert.True(viewport.Extent.Height >= 700);
        viewport.Offset = new Vector(200, 200);
        Assert.True(viewport.Offset.X > 0);
        Assert.True(viewport.Offset.Y > 0);

        host.Width = 1300;
        host.Height = 1000;
        host.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.InRange(layout.Bounds.Width, viewport.Viewport.Width - 1, viewport.Viewport.Width + 1);
        Assert.InRange(layout.Bounds.Height, viewport.Viewport.Height - 1, viewport.Viewport.Height + 1);
        Assert.Equal(900, layout.MinWidth);
        Assert.Equal(700, layout.MinHeight);
        host.Close();
    }
}
