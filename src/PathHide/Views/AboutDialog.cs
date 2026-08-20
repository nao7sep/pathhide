using Avalonia;
using Avalonia.Controls.Documents;
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
    ///
    /// The mark rides INSIDE the text as an inline, not beside it in a StackPanel:
    /// stacking centres it on the line box, which includes descender space, so it sits
    /// visibly below the capitals. An inline is placed against the text baseline, which
    /// is the only datum that holds whatever font the app is set to.
    ///
    /// Coordinates are written at the target pixel size rather than stretched, so the
    /// stroke keeps one weight — the pattern the app's XAML hamburger already uses.
    /// </summary>
    private static Control ExternalLinkLabel(string text)
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        label.Inlines!.Add(new Run(text));
        label.Inlines!.Add(new InlineUIContainer(ExternalLinkMark())
        {
            BaselineAlignment = BaselineAlignment.Baseline,
        });
        return label;
    }

    private static Shapes.Path ExternalLinkMark()
    {
        var mark = new Shapes.Path
        {
            Width = 11,
            Height = 11,
            Margin = new Thickness(5, 0, 0, 0),
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
