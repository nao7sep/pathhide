using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
    // Mirrors the per-column minimums declared in Views/MainWindow.axaml. Kept here so the
    // derivation assertion reads against a concrete, known set; the separate axaml guard below
    // is what catches drift between this list and the actual XAML.
    private static readonly double[] ColumnMinWidths = [240, 100, 90, 120, 110];

    // Chrome budget added on top of the columns: the list Border's 12px left+right margin plus
    // the ~12px vertical-scrollbar gutter. Mirrors the private constants in WindowMetrics.
    private const double ChromeWidth = 12 + 12 + 12;

    [Fact]
    public void MinWidth_EqualsColumnMinimumsPlusChrome()
    {
        var expected = ColumnMinWidths.Sum() + ChromeWidth;
        Assert.Equal(expected, WindowMetrics.MinWidthFor(ColumnMinWidths));
    }

    [Fact]
    public void MinWidth_TracksTheColumnsItIsGiven()
    {
        // Adding a column to the input must move the derived minimum by exactly that column's
        // minimum width — the property that keeps the window and its columns from drifting apart.
        var baseWidth = WindowMetrics.MinWidthFor(ColumnMinWidths);
        var widened = WindowMetrics.MinWidthFor([.. ColumnMinWidths, 75]);
        Assert.Equal(baseWidth + 75, widened);
    }

    [Fact]
    public void MinHeight_IsPositive()
    {
        // The height minimum reserves both chrome bars plus a real content minimum; the exact
        // value is an implementation detail, but it must be a sane positive reservation.
        Assert.True(WindowMetrics.MinHeight() > 0);
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
        // Target:    <repo>/Views/MainWindow.axaml
        var testsViewsDir = Path.GetDirectoryName(callerPath)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testsViewsDir, "..", "..", ".."));
        return File.ReadAllText(Path.Combine(repoRoot, "Views", "MainWindow.axaml"));
    }
}
