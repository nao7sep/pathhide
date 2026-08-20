using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

/// <summary>
/// The shared dialog shell's layout invariant: the body is the sole scroll
/// region, under a bounded height.
/// </summary>
/// <remarks>
/// The shell is SizeToContent="Height" and CanResize="False", and its footer -
/// every dismiss path it offers - is docked to the bottom. Without a bound and
/// a scroll region, a body taller than the screen pushes that footer off the
/// bottom of a window the user cannot resize or scroll. The shortcuts dialog
/// already measures close to a small laptop's working height, and the
/// startup-failure notice embeds an arbitrary-length exception message.
/// </remarks>
public sealed class DialogBaseLayoutTests
{
    [AvaloniaFact]
    public void The_body_sits_inside_a_vertical_scroll_region()
    {
        var dialog = (DialogBase)NoticeDialog.CreateStartupFailure(
            "Title",
            string.Join("\n", Enumerable.Range(0, 400).Select(i => $"line {i}")));

        var content = dialog.GetLogicalDescendants()
            .OfType<ContentPresenter>()
            .FirstOrDefault(c => c.Name == "DialogContent");
        Assert.NotNull(content);

        var scroll = content!.GetLogicalAncestors().OfType<ScrollViewer>().FirstOrDefault();
        Assert.NotNull(scroll);
        Assert.Equal(ScrollBarVisibility.Auto, scroll!.VerticalScrollBarVisibility);
        // Never a horizontal one: prose wraps, it does not scroll sideways.
        Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
    }

    [AvaloniaFact]
    public void The_footer_stays_below_the_scroll_region_rather_than_inside_it()
    {
        // If the buttons were inside the scrolled body they could be scrolled
        // out of view, which is the same failure by another route.
        var dialog = (DialogBase)NoticeDialog.CreateStartupFailure("Title", "Body");

        var footer = dialog.GetLogicalDescendants()
            .OfType<StackPanel>()
            .FirstOrDefault(p => p.Name == "ButtonPanel");
        Assert.NotNull(footer);
        Assert.Empty(footer!.GetLogicalAncestors().OfType<ScrollViewer>());
    }
}
