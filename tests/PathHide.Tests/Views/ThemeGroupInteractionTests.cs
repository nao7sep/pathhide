using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using PathHide.I18n;
using PathHide.Models;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class ThemeGroupInteractionTests : WindowTest
{
    [AvaloniaFact]
    public void Hover_keeps_the_radio_keyboard_target_and_next_arrow_and_click_use_their_own_items()
    {
        var dialog = Show(new SettingsDialog(Languages.System, "Inter", ThemePreference.Light, false, false,
            (_, _, _, _) => Task.FromResult<Message?>(null)));
        var radios = dialog.GetLogicalDescendants().OfType<RadioButton>().ToArray();
        try
        {
            dialog.GetLogicalDescendants().OfType<ComboBox>().Single().Focus(NavigationMethod.Tab);
            Press(dialog, Key.Tab);
            Assert.True(radios[1].IsFocused);
            Press(dialog, Key.Right);
            Assert.True(radios[2].IsFocused);
            Assert.True(radios[2].IsChecked);
            var earlier = Center(radios[0], dialog);
            dialog.MouseMove(earlier);
            Assert.True(radios[0].IsPointerOver);
            Assert.True(radios[2].IsFocused);
            Assert.True(radios[2].IsChecked);
            Assert.NotNull(radios[2].FocusAdorner);
            Press(dialog, Key.Left);
            Assert.True(radios[1].IsFocused);
            Assert.True(radios[1].IsChecked);
            Press(dialog, Key.Tab);
            Assert.True(dialog.GetLogicalDescendants().OfType<TextBox>().Single().IsFocused);
            Press(dialog, Key.Tab, RawInputModifiers.Shift);
            Assert.True(radios[1].IsFocused);
            dialog.MouseDown(earlier, MouseButton.Left);
            dialog.MouseUp(earlier, MouseButton.Left);
            Assert.True(radios[0].IsChecked);
        }
        finally
        {
            // Return the draft to its original value so closing cannot leave a discard dialog.
            radios[1].IsChecked = true;
        }
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point Center(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
}
