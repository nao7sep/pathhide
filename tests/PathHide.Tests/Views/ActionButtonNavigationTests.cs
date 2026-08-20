using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class ActionButtonNavigationTests
{
    [Theory]
    [InlineData(0, true, 3, 1)] // right from the first -> second
    [InlineData(2, false, 3, 1)] // left from the third -> second
    public void NextIndex_StepsToTheAdjacentButton(int current, bool forward, int count, int expected)
    {
        Assert.Equal(expected, ActionButtonNavigation.NextIndex(current, forward, count));
    }

    [Fact]
    public void NextIndex_StopsAtTheEnds_RatherThanEscapingTheGroup()
    {
        Assert.Null(ActionButtonNavigation.NextIndex(2, forward: true, count: 3)); // already last
        Assert.Null(ActionButtonNavigation.NextIndex(0, forward: false, count: 3)); // already first
    }

    [Fact]
    public void NextIndex_IsNull_WhenThereIsNoCurrentFocus()
    {
        Assert.Null(ActionButtonNavigation.NextIndex(-1, forward: true, count: 3));
        Assert.Null(ActionButtonNavigation.NextIndex(5, forward: false, count: 3));
    }

    [Fact]
    public void NextIndex_IsNull_ForASingleButton()
    {
        Assert.Null(ActionButtonNavigation.NextIndex(0, forward: true, count: 1));
        Assert.Null(ActionButtonNavigation.NextIndex(0, forward: false, count: 1));
    }

    // --- Target: the full key set the bar handles ---

    [Fact]
    public void Home_And_End_JumpToTheEnds()
    {
        Assert.Equal(0, ActionButtonNavigation.Target(ToolbarKey.Home, current: 3, count: 5));
        Assert.Equal(4, ActionButtonNavigation.Target(ToolbarKey.End, current: 3, count: 5));
    }

    [Fact]
    public void Home_And_End_AreIdempotentAtTheirOwnEnd()
    {
        Assert.Equal(0, ActionButtonNavigation.Target(ToolbarKey.Home, current: 0, count: 5));
        Assert.Equal(4, ActionButtonNavigation.Target(ToolbarKey.End, current: 4, count: 5));
    }

    [Fact]
    public void Previous_And_Next_StillStopAtTheEnds()
    {
        Assert.Null(ActionButtonNavigation.Target(ToolbarKey.Previous, current: 0, count: 5));
        Assert.Null(ActionButtonNavigation.Target(ToolbarKey.Next, current: 4, count: 5));
        Assert.Equal(2, ActionButtonNavigation.Target(ToolbarKey.Next, current: 1, count: 5));
        Assert.Equal(0, ActionButtonNavigation.Target(ToolbarKey.Previous, current: 1, count: 5));
    }

    [Fact]
    public void AnEmptyBarHasNowhereToGo()
    {
        // Every visible button can be hidden at once (the bar shows Cancel only
        // while scanning), so this is reachable rather than defensive.
        Assert.Null(ActionButtonNavigation.Target(ToolbarKey.Home, current: 0, count: 0));
        Assert.Null(ActionButtonNavigation.Target(ToolbarKey.End, current: 0, count: 0));
        Assert.Null(ActionButtonNavigation.Target(ToolbarKey.Next, current: 0, count: 0));
    }
}
