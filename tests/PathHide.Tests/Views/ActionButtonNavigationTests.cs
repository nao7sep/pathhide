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
}
