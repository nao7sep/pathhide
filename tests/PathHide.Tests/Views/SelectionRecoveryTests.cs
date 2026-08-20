using System;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class SelectionRecoveryTests
{
    [Fact]
    public void Anchor_IsTheLowestSelectedIndex()
    {
        Assert.Equal(2, SelectionRecovery.Anchor(new[] { 5, 2, 8 }));
    }

    [Fact]
    public void Anchor_IgnoresNegativeIndices_FromItemsNotInTheList()
    {
        Assert.Equal(3, SelectionRecovery.Anchor(new[] { -1, 7, 3 }));
    }

    [Fact]
    public void Anchor_IsZero_WhenNothingValidIsSelected()
    {
        Assert.Equal(0, SelectionRecovery.Anchor(Array.Empty<int>()));
        Assert.Equal(0, SelectionRecovery.Anchor(new[] { -1, -5 }));
    }

    [Theory]
    [InlineData(0, 5, 0)] // first row removed -> the row that slid up into slot 0
    [InlineData(2, 5, 2)] // a middle anchor stays put
    [InlineData(9, 5, 4)] // anchor past the end clamps to the new last row
    [InlineData(4, 5, 4)] // last remaining row
    public void TargetIndex_ClampsTheAnchorToTheRemainingRows(int anchor, int remaining, int expected)
    {
        Assert.Equal(expected, SelectionRecovery.TargetIndex(anchor, remaining));
    }

    [Fact]
    public void TargetIndex_IsMinusOne_WhenNothingRemains()
    {
        Assert.Equal(-1, SelectionRecovery.TargetIndex(0, 0));
        Assert.Equal(-1, SelectionRecovery.TargetIndex(3, 0));
    }

    // --- Anchor over a view ordering ---

    private sealed record Row(string Name);

    [Fact]
    public void Anchor_UsesTheOrderTheGridShows_NotTheOrderTheModelHolds()
    {
        // The recovered index is applied as a grid index. Rows are held in
        // insertion order while the grid is sorted from startup and re-sortable
        // on five columns, so anchoring in model order selected a different row
        // than the neighbour of the one removed.
        var z = new Row("/z");
        var a = new Row("/a");
        var m = new Row("/m");

        var insertionOrder = new[] { z, a, m };          // what Rows holds
        var viewOrder = new[] { a, m, z };               // what the grid shows

        // The user selects /z — last in the view, first in the model.
        Assert.Equal(2, SelectionRecovery.Anchor(viewOrder, new[] { z }));
        Assert.Equal(0, SelectionRecovery.Anchor(insertionOrder, new[] { z }));
    }

    [Fact]
    public void Anchor_OverAView_TakesTheLowestVisiblePositionOfTheSelection()
    {
        var a = new Row("/a");
        var m = new Row("/m");
        var z = new Row("/z");
        var viewOrder = new[] { a, m, z };

        Assert.Equal(1, SelectionRecovery.Anchor(viewOrder, new[] { z, m }));
    }

    [Fact]
    public void Anchor_OverAView_IgnoresARowTheViewDoesNotContain()
    {
        var a = new Row("/a");
        var gone = new Row("/gone");
        var viewOrder = new[] { a };

        // A stale selected item is skipped, not treated as index -1.
        Assert.Equal(0, SelectionRecovery.Anchor(viewOrder, new[] { gone, a }));
        // And when nothing matches, recovery falls back to the top.
        Assert.Equal(0, SelectionRecovery.Anchor(viewOrder, new[] { gone }));
    }
}
