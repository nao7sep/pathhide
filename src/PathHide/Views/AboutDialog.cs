using System.Reflection;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PathHide.Services;

namespace PathHide.Views;

public sealed class AboutDialog : DialogBase
{
    private const string GitHubUrl = "https://github.com/nao7sep/pathhide";

    public AboutDialog()
    {
        Width = 400;
        Title = "About PathHide";

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        var githubButton = new Button
        {
            Content = "GitHub ↗",
            Classes = { "utility" },
        };
        githubButton.Click += (_, _) => ExternalLauncher.Open(GitHubUrl);

        var issuesButton = new Button
        {
            Content = "Report Issue ↗",
            Classes = { "utility" },
        };
        issuesButton.Click += (_, _) => ExternalLauncher.Open($"{GitHubUrl}/issues");

        var panel = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                new TextBlock
                {
                    Text = "PathHide",
                    FontSize = 20,
                    FontWeight = FontWeight.Bold,
                    Margin = new Avalonia.Thickness(0, 0, 0, 4),
                },
                new TextBlock
                {
                    Text = $"Version {version}",
                    FontSize = 13,
                    Foreground = Brushes.Gray,
                    Margin = new Avalonia.Thickness(0, 0, 0, 12),
                },
                new TextBlock
                {
                    Text = "A desktop utility for macOS and Windows that hides or shows specific files and directories and remembers the desired visibility state of each entry.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Margin = new Avalonia.Thickness(0, 0, 0, 16),
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Margin = new Avalonia.Thickness(0, 0, 0, 16),
                    Children = { githubButton, issuesButton },
                },
                new TextBlock
                {
                    Text = "© 2026 Yoshinao Inoguchi — MIT License",
                    FontSize = 12,
                    Foreground = Brushes.Gray,
                },
            },
        };

        SetContent(panel);
        var buttons = SetButtons(
        [
            new DialogButton("Close", "close", DialogButtonKind.Primary) { IsDefault = true },
        ]);
        SetInitialFocus(buttons["close"]);
    }
}
