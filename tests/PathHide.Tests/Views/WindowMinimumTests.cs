using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PathHide.I18n;
using PathHide.Tests.I18n;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

/// <summary>
/// The main window's minimum follows its one-row toolbar as layout leaves it, read rather than measured,
/// so a language change moves the minimum and leaves every button in a slot of its own size.
/// </summary>
public sealed class WindowMinimumTests : WindowTest
{
    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    // What the toolbar needs, from layout as the window reads it: each button's own size, the
    // gaps and margins, the menu button and the band's padding.
    private static double ToolbarNeeds(MainWindow window)
    {
        var toolbar = window.FindControl<Border>("Toolbar")!;
        return toolbar.Padding.Left + toolbar.Padding.Right
            + window.FindControl<Button>("MenuButton")!.DesiredSize.Width
            + WindowMetrics.RowWidth(window.FindControl<StackPanel>("ActionButtons")!);
    }

    private static void AssertButtonsSitInTheirOwnSlots(MainWindow window)
    {
        foreach (var group in window.FindControl<StackPanel>("ActionButtons")!.Children.OfType<StackPanel>())
        {
            var buttons = group.Children.OfType<Button>().Where(button => button.IsVisible).ToList();
            foreach (var button in buttons)
                Assert.Equal(button.DesiredSize.Width, button.Bounds.Width, 0.5);
            foreach (var (left, right) in buttons.Zip(buttons.Skip(1)))
                Assert.Equal(left.Bounds.Right + group.Spacing, right.Bounds.Left, 0.5);
        }
    }

    [AvaloniaFact]
    public void A_language_round_trip_moves_the_minimum_with_the_toolbar_and_keeps_every_button_whole()
    {
        using var japanese = Localizer.Speaking("ja");
        var window = Show(PopulatedMainWindow.Empty());
        var layout = window.FindControl<Control>("LayoutRoot")!;
        Settle(window);
        var japaneseMinimum = layout.MinWidth;
        Assert.True(japaneseMinimum >= ToolbarNeeds(window) - 0.5);

        using (Localizer.Speaking("de"))
        {
            Settle(window);
            Assert.True(layout.MinWidth > japaneseMinimum, "German's longer labels did not widen the minimum.");
            Assert.True(layout.MinWidth >= ToolbarNeeds(window) - 0.5);
            AssertButtonsSitInTheirOwnSlots(window);
        }

        Settle(window);
        Assert.Equal(japaneseMinimum, layout.MinWidth, 0.5);
        AssertButtonsSitInTheirOwnSlots(window);
    }

    // An independent check on the window's own arithmetic: the toolbar measured whole, here in a test,
    // with nothing limiting it. A control added to the toolbar that the minimum leaves out fails this.
    [AvaloniaTheory]
    [MemberData(nameof(LabelFitTests.Tags), MemberType = typeof(LabelFitTests))]
    public void The_minimum_covers_the_whole_toolbar_measured_independently(string tag)
    {
        using var language = Localizer.Speaking(tag);
        var window = Show(PopulatedMainWindow.Empty());
        Settle(window);
        var minimum = window.FindControl<Control>("LayoutRoot")!.MinWidth;
        var derived = window.ToolbarWidth;

        var toolbar = window.FindControl<Border>("Toolbar")!;
        toolbar.Measure(Size.Infinity);

        Assert.True(minimum >= toolbar.DesiredSize.Width - 0.5,
            $"{tag}: the minimum {minimum:0} px is narrower than the toolbar's {toolbar.DesiredSize.Width:0} px.");
        // Before the columns' floor, which in a language whose toolbar is narrower could hide a term the
        // arithmetic leaves out.
        Assert.Equal(toolbar.DesiredSize.Width, derived, 0.5);
    }

    [AvaloniaFact]
    public void A_row_needs_its_shown_children_its_gaps_and_its_margin()
    {
        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 16, 0),
            Children =
            {
                new Border { Width = 40 },
                new Border { Width = 60 },
                new Border { Width = 99, IsVisible = false },
            },
        };
        var window = Show(new Window { Content = new Border { Width = 50, Child = row } });
        Settle(window);

        // The panel itself is held to 50, but its children asked for their own widths.
        Assert.Equal(40 + 8 + 60 + 16, WindowMetrics.RowWidth(row));
    }
}
