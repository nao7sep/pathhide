using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.VisualTree;
using PathHide.I18n;
using PathHide.Tests.I18n;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

/// <summary>
/// The menu's longest label sits as far from the menu's right edge as every label sits from its left.
/// Fluent reserves a gesture column after each label, led by a margin even when the item has no gesture;
/// <see cref="MenuGestureColumn"/> removes that margin from items without one and keeps it for items
/// with one.
/// </summary>
public class MenuLayoutTests : WindowTest
{
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("ru")]
    [InlineData("ja")]
    public void the_menu_leaves_no_more_room_after_its_labels_than_before_them(string tag)
    {
        using var speaking = Localizer.Speaking(tag);
        var window = Show(PopulatedMainWindow.Empty());
        var button = window.FindControl<Button>("MenuButton")!;
        var flyout = (MenuFlyout)button.Flyout!;
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();

        try
        {
            var presenter = ((MenuItem)flyout.Items[0]!).FindAncestorOfType<MenuFlyoutPresenter>()!;
            var labels = presenter.GetVisualDescendants().OfType<MenuItem>()
                .Select(item => item.GetVisualDescendants().OfType<ContentPresenter>()
                    .Single(content => content.Name == "PART_HeaderPresenter")
                    .GetVisualDescendants().OfType<TextBlock>().First())
                .ToList();
            Assert.Equal(4, labels.Count);

            var leftGap = labels.Min(label => label.TranslatePoint(default, presenter)!.Value.X);
            var rightGap = presenter.Bounds.Width - labels.Max(label =>
                label.TranslatePoint(default, presenter)!.Value.X + label.TextLayout.WidthIncludingTrailingWhitespace);

            Assert.True(
                Math.Abs(rightGap - leftGap) <= 1,
                $"{tag}: {rightGap:F1}px after the longest label, {leftGap:F1}px before each label.");
        }
        finally
        {
            flyout.Hide();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void an_item_keeps_the_gap_before_its_gesture_and_loses_it_with_its_gesture()
    {
        var item = new MenuItem { Header = "Cut" };
        var flyout = new MenuFlyout { Items = { item } };
        var target = new Button { Content = "target" };
        Show(new Window { Content = target });
        flyout.ShowAt(target);
        Dispatcher.UIThread.RunJobs();

        try
        {
            var gestureText = item.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Name == "PART_InputGestureText");
            Assert.Equal(default, gestureText.Margin);

            item.InputGesture = new KeyGesture(Key.X, KeyModifiers.Control);
            Assert.True(gestureText.Margin.Left > 0, "an item with a gesture lost the gap before it");

            item.InputGesture = null;
            Assert.Equal(default, gestureText.Margin);
        }
        finally
        {
            flyout.Hide();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
