using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace PathHide.Tests;

public sealed class AppStylesTests
{
    [AvaloniaFact]
    public void A_scroll_bar_takes_its_own_width_instead_of_drawing_over_the_content()
    {
        // No width of its own: it takes the room the viewer leaves, which is the measurement.
        var content = new Border { Height = 400 };
        var viewer = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var window = new Window { Content = viewer, Width = 200, Height = 120 };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var bar = viewer.GetVisualDescendants().OfType<ScrollBar>()
                .Single(candidate => candidate.Orientation == Avalonia.Layout.Orientation.Vertical
                    && candidate.Bounds.Width > 0);

            Assert.False(viewer.AllowAutoHide);
            Assert.True(viewer.Extent.Height > viewer.Viewport.Height, "the viewer must actually overflow");

            var contentRight = content.TranslatePoint(new Point(content.Bounds.Width, 0), viewer)!.Value.X;
            var barLeft = bar.TranslatePoint(new Point(0, 0), viewer)!.Value.X;
            Assert.True(contentRight <= barLeft + 0.5,
                $"the content reaches {contentRight:F0} and the bar starts at {barLeft:F0}, so the bar covers it");
        }
        finally
        {
            window.Close();
        }
    }

    // A state a class does not state at presenter level is answered by Fluent there instead, and
    // its answers replace rather than step: every colour code became the same 40% black wash under
    // the finger, and the same grey slab when switched off, so a disabled Hide and a disabled
    // Reload were one control. The pseudo-classes are set after the window is shown and the
    // template applied, because the control resets them on attach.
    [AvaloniaTheory]
    [InlineData("add", "AddPressedBrush")]
    [InlineData("hide", "HidePressedBrush")]
    [InlineData("show", "ShowPressedBrush")]
    [InlineData("reload", "ReloadPressedBrush")]
    [InlineData("reapply", "ReapplyPressedBrush")]
    [InlineData("destructive", "DangerPressedBrush")]
    [InlineData("cancel", "CancelPressedBrush")]
    [InlineData("utility", "UtilityPressedBrush")]
    [InlineData("accent", "ReloadPressedBrush")]
    public void A_pressed_button_is_a_step_of_its_own_fill(string variant, string pressedBrush)
    {
        var resting = Classed(variant);
        var hovered = Classed(variant);
        var pressed = Classed(variant);
        var window = new Window { Content = new StackPanel { Children = { resting, hovered, pressed } } };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            ((IPseudoClasses)hovered.Classes).Set(":pointerover", true);
            ((IPseudoClasses)pressed.Classes).Set(":pressed", true);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Color(Brush(pressedBrush)), Fill(pressed));
            Assert.NotEqual(Fill(resting), Fill(pressed));
            Assert.NotEqual(Fill(hovered), Fill(pressed));
        }
        finally
        {
            window.Close();
        }
    }

    // The dialog's primary shares its class name with one Fluent ships, whose presenter setter
    // outranks a button-level fill — so it rested at the toolkit's system azure and only reached
    // the app's blue once the pointer arrived.
    [AvaloniaFact]
    public void The_dialog_primary_rests_on_the_app_s_own_blue()
    {
        var button = Classed("accent");
        var window = new Window { Content = button };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Color(Brush("ReloadBrush")), Fill(button));
        }
        finally
        {
            window.Close();
        }
    }

    // Off, a button is its resting self faded, so the colour codes stay told apart while they are
    // off. Only the classes a command or a binding can switch off restate their fill; the rest say
    // nothing, because nothing disables them.
    [AvaloniaTheory]
    [InlineData("hide")]
    [InlineData("show")]
    [InlineData("reload")]
    [InlineData("reapply")]
    [InlineData("accent")]
    public void A_disabled_button_keeps_its_own_fill(string variant)
    {
        var resting = Classed(variant);
        var off = Classed(variant);
        off.IsEnabled = false;
        var window = new Window { Content = new StackPanel { Children = { resting, off } } };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Fill(resting), Fill(off));
            Assert.Equal(Ink(resting), Ink(off));
            Assert.Equal(1d, resting.Opacity);
            Assert.True(off.Opacity < 1d, "a disabled button recedes");
        }
        finally
        {
            window.Close();
        }
    }

    private static Button Classed(string variant)
    {
        var button = new Button { Content = "Reload" };
        button.Classes.Add(variant);
        return button;
    }

    private static ContentPresenter Presenter(Button button) =>
        button.GetVisualDescendants().OfType<ContentPresenter>().First();

    private static string Fill(Button button) => Color(Presenter(button).Background);

    private static string Ink(Button button) => Color(Presenter(button).Foreground);

    private static string Color(IBrush? brush) =>
        brush is ISolidColorBrush solid ? solid.Color.ToString() : $"<{brush?.GetType().Name ?? "null"}>";

    private static IBrush Brush(string key)
    {
        var app = Application.Current!;
        return (IBrush)app.FindResource(app.ActualThemeVariant, key)!;
    }

    private static ScrollViewer OverflowingViewer() => new()
    {
        Content = new Border { Width = 400, Height = 400 },
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
}
