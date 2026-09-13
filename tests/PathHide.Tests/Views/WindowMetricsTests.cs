using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

/// <summary>
/// The window's minimum size is derived, not guessed (per the window-chrome conventions):
/// <see cref="WindowMetrics"/> sums the live DataGrid column minimums plus fixed chrome so the
/// window can never shrink small enough to hide the toolbar, list, or status bar. These tests
/// pin the derivation math directly (no Avalonia headless harness, matching the suite's
/// pure-helper style) and guard that every grid column declares a non-zero minimum width — so a
/// future column added without one fails here rather than silently letting the window under-size.
/// </summary>
public sealed class WindowMetricsTests
{
    [Theory]
    [InlineData(false, false, Avalonia.Controls.WindowState.Normal)]
    [InlineData(true, false, Avalonia.Controls.WindowState.Normal)]
    [InlineData(false, true, Avalonia.Controls.WindowState.Normal)]
    [InlineData(true, true, Avalonia.Controls.WindowState.Maximized)]
    public void Restored_window_state_is_maximized_only_on_Windows(
        bool maximized, bool isWindows, Avalonia.Controls.WindowState expected)
    {
        Assert.Equal(expected, WindowMetrics.RestoredWindowState(maximized, isWindows));
    }

    [Fact]
    public void Saved_window_geometry_accepts_negative_coordinates_on_a_current_screen()
    {
        Avalonia.PixelRect[] workingAreas =
        [
            new(-1920, -1080, 1920, 1080),
            new(0, 0, 2560, 1440),
        ];

        Assert.True(WindowMetrics.CanRestoreWindowGeometry(-1800, -1000, 1280, 720, workingAreas));
        Assert.True(WindowMetrics.CanRestoreWindowGeometry(0, 0, 1280, 720, workingAreas));
        Assert.False(WindowMetrics.CanRestoreWindowGeometry(-2500, 0, 1280, 720, workingAreas));
        Assert.False(WindowMetrics.CanRestoreWindowGeometry(2560, 0, 1280, 720, workingAreas));
    }

    [Theory]
    [InlineData(null, 0, 1280d, 720d)]
    [InlineData(0, null, 1280d, 720d)]
    [InlineData(0, 0, null, 720d)]
    [InlineData(0, 0, 1280d, null)]
    [InlineData(0, 0, 0d, 720d)]
    [InlineData(0, 0, 1280d, -1d)]
    [InlineData(0, 0, double.PositiveInfinity, 720d)]
    public void Saved_window_geometry_rejects_missing_or_invalid_primitives(
        int? x, int? y, double? width, double? height)
    {
        Assert.False(WindowMetrics.CanRestoreWindowGeometry(
            x, y, width, height, [new Avalonia.PixelRect(0, 0, 1920, 1080)]));
    }

    [Fact]
    public void Native_minimum_uses_scaled_work_area_without_changing_the_content_floor()
    {
        var floor = new Avalonia.Size(1200, 800);
        var capped = WindowMetrics.CapMinimumToWorkArea(
            floor, new Avalonia.PixelRect(-1920, 40, 1600, 1000), 2, new Avalonia.Size(8, 30));
        Assert.Equal(new Avalonia.Size(792, 470), capped);
        Assert.Equal(new Avalonia.Size(1200, 800), floor);
        Assert.Equal(floor, WindowMetrics.CapMinimumToWorkArea(
            floor, new Avalonia.PixelRect(0, 0, 3840, 2160), 2, new Avalonia.Size(8, 30)));
    }

    // Mirrors the per-column minimums declared in Views/MainWindow.axaml. Kept here so the
    // derivation assertion reads against a concrete, known set; the separate axaml guard below
    // is what catches drift between this list and the actual XAML.
    private static readonly double[] ColumnMinWidths = [240, 100, 90, 120, 110];

    // The list Border's 12px left+right margin is the only fixed chrome in WindowMetrics.
    // The scrollbar gutter comes from the live Fluent theme and is passed in by the view.
    private const double GridHorizontalMargin = 12 + 12;
    private const double ScrollBarGutter = 16;

    [Fact]
    public void MinWidth_EqualsColumnMinimumsPlusChrome()
    {
        var expected = ColumnMinWidths.Sum() + GridHorizontalMargin + ScrollBarGutter;
        Assert.Equal(expected, WindowMetrics.MinWidthFor(ColumnMinWidths, ScrollBarGutter));
    }

    [Fact]
    public void MinWidth_TracksTheColumnsItIsGiven()
    {
        // Adding a column to the input must move the derived minimum by exactly that column's
        // minimum width — the property that keeps the window and its columns from drifting apart.
        var baseWidth = WindowMetrics.MinWidthFor(ColumnMinWidths, ScrollBarGutter);
        var widened = WindowMetrics.MinWidthFor([.. ColumnMinWidths, 75], ScrollBarGutter);
        Assert.Equal(baseWidth + 75, widened);
    }

    [Fact]
    public void MinWidth_TracksTheLiveScrollBarGutter()
    {
        var narrowTheme = WindowMetrics.MinWidthFor(ColumnMinWidths, verticalScrollBarGutter: 12);
        var wideTheme = WindowMetrics.MinWidthFor(ColumnMinWidths, verticalScrollBarGutter: 20);

        Assert.Equal(narrowTheme + 8, wideTheme);
    }

    [AvaloniaFact]
    public void FluentTheme_ProvidesTheLiveScrollBarGutter()
    {
        Assert.True(Application.Current!.TryGetResource(
            "ScrollBarSize",
            ThemeVariant.Light,
            out var value));
        Assert.True(Assert.IsType<double>(value) > 0);
    }

    [Fact]
    public void MinHeight_IsTheMeasuredChromePlusTheContentMinimum()
    {
        // The chrome heights are measured from the live controls and passed in, because both
        // depend on the user-configurable UI font — the same reason the width is measured.
        var withSmallChrome = WindowMetrics.MinHeightFor(toolbarHeight: 52, statusBarHeight: 33);
        var withLargeChrome = WindowMetrics.MinHeightFor(toolbarHeight: 72, statusBarHeight: 44);

        Assert.True(withSmallChrome > 0);
        // A taller chrome raises the minimum by exactly its extra height, so the content pane
        // keeps its declared minimum rather than being squeezed by a bigger font.
        Assert.Equal(withSmallChrome + 31, withLargeChrome);
    }

    [Fact]
    public void EveryDataGridColumn_DeclaresANonZeroMinWidth()
    {
        // Guard against a column being added without a MinWidth: such a column would contribute
        // 0 to the derived window minimum and could be squeezed to invisibility. Read the live
        // XAML so this fails the moment a real column is added without a minimum.
        var minWidths = DataGridColumnMinWidths(ReadMainWindowAxaml());

        Assert.NotEmpty(minWidths);
        Assert.All(minWidths, m => Assert.True(m > 0, "A DataGrid column is missing a non-zero MinWidth."));
    }

    [Fact]
    public void DerivedMinWidth_MatchesTheLiveColumnMinimums()
    {
        // The mirrored ColumnMinWidths used above must stay equal to what the XAML actually
        // declares, so the derivation test cannot pass against a stale list.
        var fromXaml = DataGridColumnMinWidths(ReadMainWindowAxaml());
        Assert.Equal(ColumnMinWidths, fromXaml);
    }

    private static IReadOnlyList<double> DataGridColumnMinWidths(string axaml) =>
        Regex.Matches(axaml, "<DataGridTextColumn\\b[^>]*?MinWidth=\"(?<min>\\d+(?:\\.\\d+)?)\"")
            .Select(m => double.Parse(m.Groups["min"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

    private static string ReadMainWindowAxaml([CallerFilePath] string callerPath = "")
    {
        // This file: <repo>/tests/PathHide.Tests/Views/WindowMetricsTests.cs
        // Target:    <repo>/src/PathHide/Views/MainWindow.axaml
        var testsViewsDir = Path.GetDirectoryName(callerPath)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testsViewsDir, "..", "..", ".."));
        return File.ReadAllText(Path.Combine(repoRoot, "src", "PathHide", "Views", "MainWindow.axaml"));
    }
}
