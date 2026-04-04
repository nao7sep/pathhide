using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PathHide.Views;

public partial class DialogBase : Window
{
    public string? ResultTag { get; private set; }

    public DialogBase()
    {
        InitializeComponent();
    }

    protected void SetContent(Control content)
    {
        DialogContent.Content = content;
    }

    protected void SetButtons(IEnumerable<(string Label, string Tag, bool IsDefault)> buttons)
    {
        ButtonPanel.Children.Clear();

        foreach (var (label, tag, isDefault) in buttons)
        {
            var button = new Button
            {
                Content = label,
                Tag = tag,
                MinWidth = 80,
            };

            if (isDefault)
                button.Classes.Add("accent");

            button.Click += OnButtonClick;
            ButtonPanel.Children.Add(button);
        }
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
