using Shapes = Avalonia.Controls.Shapes;
using Avalonia.Data;
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
            Content = ExternalLinkLabel("GitHub"),
            Classes = { "utility" },
        };
        githubButton.Click += (_, _) => ExternalLauncher.Open(GitHubUrl);

        var issuesButton = new Button
        {
            Content = ExternalLinkLabel("Report Issue"),
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

    /// <summary>
    /// A button label with a trailing external-link mark drawn as a vector rather than
    /// the ↗ glyph, whose weight and size vary by font. The mark binds to the button's
    /// own foreground, so it follows theme and hover exactly as the text does.
    /// Coordinates are written at the target pixel size — Avalonia's house pattern here
    /// — so the stroke keeps a constant weight instead of being scaled by a Stretch.
    /// </summary>
    private static Control ExternalLinkLabel(string text) =>
        new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
                ExternalLinkMark(),
            },
        };

    private static Shapes.Path ExternalLinkMark()
    {
        var mark = new Shapes.Path
        {
            Width = 11,
            Height = 11,
            VerticalAlignment = VerticalAlignment.Center,
            StrokeThickness = 1.3,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            UseLayoutRounding = true,
            Data = Geometry.Parse("M7.8,6.1 V10.35 H0.65 V3.2 H5.0 M6.3,0.65 H10.35 V4.7 M10.35,0.65 L5.2,5.8"),
        };
        mark.Bind(
            Shapes.Shape.StrokeProperty,
            new Binding("Foreground") { RelativeSource = new RelativeSource { AncestorType = typeof(Button) } });
        return mark;
    }

}
