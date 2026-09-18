using System;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

/// <summary>
/// The macOS emoji picker hands its choice back while the window is in the background, after Avalonia
/// has dropped the window's focused element, so the text is raised on the window itself. These check
/// that such text reaches the text field that had focus, at its caret, and that nothing else changes.
/// The handler is installed on macOS only, so the rest skip elsewhere.
/// </summary>
public sealed class BackgroundTextInputTests
{
    [AvaloniaFact]
    public void The_handler_is_installed_on_macOS_only()
    {
        BackgroundTextInput.Install();
        Assert.Equal(OperatingSystem.IsMacOS(), BackgroundTextInput.Installed);
    }

    [AvaloniaFact]
    public void Text_raised_on_a_background_window_goes_to_its_last_text_field_at_the_caret()
    {
        using var scene = new Scene("ab");
        scene.Field.CaretIndex = 1;
        scene.PutBehind();
        scene.DropFocus();

        var e = scene.RaiseText(scene.Window, "😀");

        Assert.Equal("a😀b", scene.Field.Text);
        Assert.True(e.Handled);
        Assert.True(scene.Field.IsFocused);
    }

    [AvaloniaFact]
    public void A_window_in_front_with_nothing_focused_is_left_alone()
    {
        using var scene = new Scene("ab");
        scene.DropFocus();

        var e = scene.RaiseText(scene.Window, "x");

        Assert.Equal("ab", scene.Field.Text);
        Assert.False(e.Handled);
    }

    [AvaloniaFact]
    public void Text_raised_on_another_focused_control_is_left_alone()
    {
        using var scene = new Scene("ab");
        scene.PutBehind();
        scene.Button.Focus();
        Dispatcher.UIThread.RunJobs();

        scene.RaiseText(scene.Button, "x");

        Assert.Equal("ab", scene.Field.Text);
    }

    [AvaloniaFact]
    public void A_read_only_field_or_one_that_left_the_window_does_not_take_it()
    {
        using var scene = new Scene("ab");
        var other = new Window { Content = new StackPanel() };
        try
        {
            scene.PutBehind();
            scene.DropFocus();
            scene.Field.IsReadOnly = true;
            scene.RaiseText(scene.Window, "x");
            Assert.Equal("ab", scene.Field.Text);

            scene.Field.IsReadOnly = false;
            ((StackPanel)scene.Window.Content!).Children.Remove(scene.Field);
            Dispatcher.UIThread.RunJobs();
            scene.RaiseText(scene.Window, "x");
            Assert.Equal("ab", scene.Field.Text);

            // Moved into another window, it is that window's field now.
            other.Show();
            ((StackPanel)other.Content!).Children.Add(scene.Field);
            Dispatcher.UIThread.RunJobs();
            scene.RaiseText(scene.Window, "x");
            Assert.Equal("ab", scene.Field.Text);
        }
        finally
        {
            other.Close();
        }
    }

    [AvaloniaFact]
    public void Once_Avalonia_keeps_the_field_focused_the_text_is_inserted_once()
    {
        // The day Avalonia stops dropping focus (issue 20616), the text reaches the field itself and
        // bubbles to the window already handled, so this workaround stays out of the way.
        using var scene = new Scene("ab");
        scene.Field.CaretIndex = 2;
        scene.PutBehind();

        scene.Window.KeyTextInput("!");

        Assert.Equal("ab!", scene.Field.Text);
    }

    // A window with a text field and a button, the field focused, the workaround installed; disposing
    // it closes the window and puts back whether windows count as in front.
    private sealed class Scene : IDisposable
    {
        private readonly Func<WindowBase, bool> _isInFront = BackgroundTextInput.IsInFront;

        public Scene(string text)
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(), "The handler is installed on macOS only.");
            BackgroundTextInput.Install();
            Field = new TextBox { Text = text };
            Button = new Button { Content = "Other" };
            Window = new Window { Content = new StackPanel { Children = { Field, Button } } };
            Window.Show();
            Field.Focus();
            Dispatcher.UIThread.RunJobs();
        }

        public Window Window { get; }

        public TextBox Field { get; }

        public Button Button { get; }

        // As when the picker has the keyboard; a headless window cannot be put behind.
        public void PutBehind() => BackgroundTextInput.IsInFront = window => window != Window;

        // What Avalonia does when the window loses the keyboard: the focused element is cleared.
        public void DropFocus()
        {
            Window.FocusManager!.Focus(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(Window.FocusManager.GetFocusedElement());
        }

        public TextInputEventArgs RaiseText(Control source, string text)
        {
            var e = new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text };
            source.RaiseEvent(e);
            return e;
        }

        public void Dispose()
        {
            BackgroundTextInput.IsInFront = _isInFront;
            Window.Close();
        }
    }
}
