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
}
