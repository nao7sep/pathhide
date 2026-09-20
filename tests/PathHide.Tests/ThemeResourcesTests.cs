using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using PathHide.Models;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests;

public sealed class ThemeResourcesTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData(ThemePreference.System, "Default")]
    [InlineData(ThemePreference.Light, "Light")]
    [InlineData(ThemePreference.Dark, "Dark")]
    public void EachPreferenceMapsToOneThemeVariant(ThemePreference preference, string variant) =>
        Assert.Equal(variant, AppTheme.VariantFor(preference).Key.ToString());

    [Fact]
    public void ANewSettingsFileStartsOnSystem() =>
        Assert.Equal(ThemePreference.System, new AppSettings().Theme);

    [Fact]
    public void LightAndDarkDefineTheSameThemedBrushes()
    {
        var light = ThemeBrushes("Light");
        var dark = ThemeBrushes("Dark");
        Assert.NotEmpty(light);
        Assert.Equal(light.Keys.OrderBy(key => key), dark.Keys.OrderBy(key => key));
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void TextKeepsHighContrastInEachTheme(string theme)
    {
        var brushes = ThemeBrushes(theme);
        foreach (var text in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "StatusAccentBrush", "DangerTextBrush" })
        {
            foreach (var surface in new[] { "AppBackgroundBrush", "SurfaceBrush", "StatusBackgroundBrush" })
            {
                var ratio = Contrast(brushes[text], brushes[surface]);
                Assert.True(ratio >= 4.5, $"{theme}: {text} on {surface} is {ratio:F2}:1");
            }
        }
    }

    [Fact]
    public void WhiteLabelsKeepHighContrastOnEveryActionFill()
    {
        // Every rung of every ladder, not just the resting one: a button's label is read on
        // the fill it hovers to and the fill it presses to as surely as on the one it sits at.
        var actions = new[] { "Add", "Hide", "Show", "Reload", "Reapply", "Danger", "Cancel", "Utility" };
        var fills = actions
            .SelectMany(action => new[] { action, $"{action}Hover", $"{action}Pressed" })
            .Append("InactiveAction");
        var colors = RootColors();
        foreach (var fill in fills)
        {
            var ratio = Contrast(Colors.White, colors[fill]);
            Assert.True(ratio >= 4.5, $"white on {fill} is {ratio:F2}:1");
        }
    }

    // A press steps the fill one rung further down the ladder its hover already steps, so the
    // colour code survives the click. Left unsaid, Fluent answered instead, and its answer is not
    // a step but a replacement: a 40% black wash with the label forced to black, identical for
    // green, pink, violet, blue, teal, red and amber alike.
    [Fact]
    public void EveryActionFillStatesAPressedStepBeyondItsHover()
    {
        var colors = RootColors();
        foreach (var action in new[] { "Add", "Hide", "Show", "Reload", "Reapply", "Danger", "Cancel", "Utility" })
        {
            Assert.True(colors.ContainsKey($"{action}Pressed"), $"{action} states no pressed fill");
            Assert.True(
                Luminance(colors[$"{action}Pressed"]) < Luminance(colors[$"{action}Hover"]),
                $"{action}: pressed must be a step beyond hover, in the hover's own direction");
        }
    }

    [AvaloniaFact]
    public void CodeBuiltAndMarkupSurfacesRepaintWhenTheThemeChanges()
    {
        var app = Application.Current!;
        var dialog = new ShortcutsDialog(ShortcutCatalog.Build(new Window()));
        try
        {
            AppTheme.Apply(ThemePreference.Light);
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            var light = CardBackgrounds(dialog);

            AppTheme.Apply(ThemePreference.Dark);
            Dispatcher.UIThread.RunJobs();
            var dark = CardBackgrounds(dialog);

            Assert.Equal(ThemeBrushes("Light")["SurfaceBrush"], Assert.Single(light.Distinct()));
            Assert.Equal(ThemeBrushes("Dark")["SurfaceBrush"], Assert.Single(dark.Distinct()));
            Assert.Equal(ThemeBrushes("Dark")["AppBackgroundBrush"], ((ISolidColorBrush)dialog.Background!).Color);
        }
        finally
        {
            dialog.Close();
            app.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    private static List<Color> CardBackgrounds(Window dialog) =>
        dialog.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.CornerRadius == new CornerRadius(8))
            .Select(border => ((ISolidColorBrush)border.Background!).Color)
            .ToList();

    private static XDocument AppXaml() =>
        XDocument.Load(Path.Combine(RepoRoot(), "src", "PathHide", "App.axaml"));

    private static Dictionary<string, Color> ThemeBrushes(string theme) =>
        AppXaml().Descendants()
            .Single(element => element.Name.LocalName == "ResourceDictionary"
                && (string?)element.Attribute(X + "Key") == theme)
            .Elements()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .ToDictionary(
                element => (string)element.Attribute(X + "Key")!,
                element => Color.Parse((string)element.Attribute("Color")!));

    private static Dictionary<string, Color> RootColors() =>
        AppXaml().Descendants()
            .Where(element => element.Name.LocalName == "Color")
            .ToDictionary(element => (string)element.Attribute(X + "Key")!, element => Color.Parse(element.Value));

    private static string RepoRoot([CallerFilePath] string callerPath = "")
    {
        // This file: <repo>/tests/PathHide.Tests/ThemeResourcesTests.cs
        var testsProjectDir = Path.GetDirectoryName(callerPath)!;
        return Path.GetFullPath(Path.Combine(testsProjectDir, "..", ".."));
    }

    private static double Contrast(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
}
