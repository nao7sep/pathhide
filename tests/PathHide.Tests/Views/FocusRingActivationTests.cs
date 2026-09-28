using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace PathHide.Tests.Views;

/// <summary>
/// One focus treatment for the whole app softens while its window is inactive
/// (interface-styling conventions). Headless has no real window manager to hand activation to
/// another window, so a test fires the platform's own Deactivated callback directly — the same
/// callback a real OS deactivation invokes (<c>WindowBase.HandleDeactivated</c>).
/// </summary>
public sealed class FocusRingActivationTests
{
    [AvaloniaFact]
    public void A_focused_button_s_ring_softens_when_its_window_goes_inactive()
    {
        var button = new Button { Content = "Reload" };
        var window = new Window { Content = button };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            button.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsActive);
            Assert.Equal(Color("FocusRingBrush"), RingColor(button));

            Deactivate(window);

            Assert.False(window.IsActive);
            Assert.Equal(Color("FocusRingInactiveBrush"), RingColor(button));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MainWindow_marks_itself_inactive_for_its_field_focus_ring_too()
    {
        // TextBox and ComboBox draw their focus edge as a template part rather than an adorner,
        // through the windowInactive class MainWindow now sets alongside DialogBase.
        var field = new TextBox();
        var window = new Window { Content = field };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            field.Focus();
            Dispatcher.UIThread.RunJobs();

            var border = field.GetVisualDescendants().OfType<Border>()
                .First(b => b.Name == "PART_BorderElement");
            Assert.Equal(Color("FocusRingBrush"), Color(border.BorderBrush));

            window.Classes.Set("windowInactive", true);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Color("FocusRingInactiveBrush"), Color(border.BorderBrush));
        }
        finally
        {
            window.Close();
        }
    }

    // Simulates a real OS deactivation: invokes the same callback WindowBase wires to the
    // platform window's Deactivated slot (there is no second real window here to hand
    // activation to, as there would be on a desktop backend, and the interface member that
    // slot lives on is internal to Avalonia, so reflection reaches it instead of a cast).
    private static void Deactivate(Window window)
    {
        var property = typeof(IWindowBaseImpl).GetProperty(
            "Deactivated", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        var callback = (System.Action?)property.GetValue(window.PlatformImpl);
        callback?.Invoke();
        Dispatcher.UIThread.RunJobs();
    }

    private static string RingColor(Button button)
    {
        var adorner = AdornerLayer.GetAdornerLayer(button)!
            .GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("focusRing"));
        return Color(adorner.BorderBrush);
    }

    private static string Color(string resourceKey) => Color(Brush(resourceKey));

    private static IBrush Brush(string key)
    {
        var app = Application.Current!;
        return (IBrush)app.FindResource(app.ActualThemeVariant, key)!;
    }

    private static string Color(IBrush? brush) =>
        brush is ISolidColorBrush solid ? solid.Color.ToString() : $"<{brush?.GetType().Name ?? "null"}>";
}
