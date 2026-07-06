using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PathHide.Controls;
using Xunit;

namespace PathHide.Tests.Controls;

public sealed class ImeTextBoxTests
{
    private static T Host<T>(T content) where T : Control
    {
        var window = new Window { Content = content, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return content;
    }

    private static TextPresenter Presenter(ImeTextBox box) =>
        box.GetVisualDescendants().OfType<TextPresenter>().Single(p => p.Name == "PART_TextPresenter");

    private static TextInputMethodClient InputMethodClient(ImeTextBox box)
    {
        var e = new TextInputMethodClientRequestedEventArgs
        {
            RoutedEvent = InputElement.TextInputMethodClientRequestedEvent,
        };
        box.RaiseEvent(e);
        return Assert.IsAssignableFrom<TextInputMethodClient>(e.Client);
    }

    [AvaloniaFact]
    public void Enter_remains_unhandled_for_the_dialog_default_button()
    {
        var box = Host(new ImeTextBox());
        var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };

        box.RaiseEvent(e);

        Assert.False(e.Handled);
    }

    [AvaloniaFact]
    public void MacOs_input_client_reports_the_text_box_as_its_coordinate_visual()
    {
        var box = Host(new ImeTextBox());
        var client = InputMethodClient(box);

        if (OperatingSystem.IsMacOS())
        {
            Assert.Same(box, client.TextViewVisual);
        }
        else
        {
            Assert.Same(Presenter(box), client.TextViewVisual);
        }
    }

    [AvaloniaFact]
    public void MacOs_input_client_preserves_preedit_and_cursor_notifications()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var box = Host(new ImeTextBox { Text = "abc" });
        var presenter = Presenter(box);
        var client = InputMethodClient(box);
        var cursorChanges = 0;
        client.CursorRectangleChanged += (_, _) => cursorChanges++;

        client.SetPreeditText("にほん", 2);

        Assert.Equal("にほん", presenter.PreeditText);
        Assert.True(cursorChanges > 0);
    }

    [AvaloniaFact]
    public void MacOs_input_client_refreshes_its_cursor_rectangle_when_the_text_box_scrolls()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var box = Host(new ImeTextBox
        {
            AcceptsReturn = true,
            Height = 80,
            Text = string.Join('\n', Enumerable.Repeat("line", 30)),
        });
        var scrollViewer = box.GetVisualDescendants().OfType<ScrollViewer>().Single();
        var client = InputMethodClient(box);
        var cursorChanges = 0;
        client.CursorRectangleChanged += (_, _) => cursorChanges++;

        scrollViewer.Offset = new Avalonia.Vector(0, 100);
        Dispatcher.UIThread.RunJobs();

        Assert.True(cursorChanges > 0);
    }
}
