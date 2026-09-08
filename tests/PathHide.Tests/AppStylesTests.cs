using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace PathHide.Tests;

public sealed class AppStylesTests
{
    [AvaloniaFact]
    public void Scrollbars_use_the_shared_native_hover_and_leave_timing()
    {
        var viewer = OverflowingViewer();
        var window = new Window { Content = viewer, Width = 120, Height = 120 };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var bars = viewer.GetVisualDescendants().OfType<ScrollBar>().ToList();

            Assert.True(viewer.AllowAutoHide);
            Assert.NotEmpty(bars);
            Assert.All(bars, bar =>
            {
                Assert.True(bar.AllowAutoHide);
                Assert.Equal(TimeSpan.Zero, bar.ShowDelay);
                Assert.Equal(TimeSpan.FromSeconds(2), bar.HideDelay);
            });
        }
        finally
        {
            window.Close();
        }
    }

    private static ScrollViewer OverflowingViewer() => new()
    {
        Content = new Border { Width = 400, Height = 400 },
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
}
