using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace PathHide.Views;

public partial class DialogBase : Window
{
    private Control? _initialFocusControl;

    public string? ResultTag { get; private set; }

    public DialogBase()
    {
        InitializeComponent();
        Opened += OnOpened;
        KeyDown += OnKeyDown;
    }

    protected void SetContent(Control content)
    {
        DialogContent.Content = content;
    }

    protected IReadOnlyDictionary<string, Button> SetButtons(IEnumerable<(string Label, string Tag, bool IsDefault)> buttons)
    {
        ButtonPanel.Children.Clear();
        var createdButtons = new Dictionary<string, Button>();

        foreach (var (label, tag, isDefault) in buttons)
        {
            var button = new Button
            {
                Content = label,
                Tag = tag,
                MinWidth = 80,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };

            if (isDefault)
                button.Classes.Add("accent");

            button.Click += OnButtonClick;
            ButtonPanel.Children.Add(button);
            createdButtons[tag] = button;
        }

        return createdButtons;
    }

    protected void SetInitialFocus(Control control)
    {
        _initialFocusControl = control;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (_initialFocusControl is null)
            return;

        Dispatcher.UIThread.Post(() => _initialFocusControl.Focus());
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            ResultTag = button.Tag as string;
            Close();
        }
    }
}
