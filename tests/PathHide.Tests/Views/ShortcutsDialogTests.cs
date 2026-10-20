using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class ShortcutsDialogTests : WindowTest
{
    [Theory]
    [InlineData(new[] { 3, 2, 3, 3, 2 }, 2)] // 5 | 8
    [InlineData(new[] { 1, 1 }, 1)]
    [InlineData(new[] { 5, 1, 1, 1 }, 1)] // 5 | 3 beats 6 | 2
    [InlineData(new[] { 3 }, 0)] // one section: either side holds 3, the earlier split wins
    [InlineData(new int[0], 0)]
    public void Sections_split_where_the_taller_column_is_shortest(int[] rows, int expected) =>
        Assert.Equal(expected, ShortcutsDialog.BalancedSplit(rows));

    [AvaloniaFact]
    public void The_dialog_fits_within_the_main_windows_default_height()
    {
        var owner = Show(new Window());
        var dialog = Show(new ShortcutsDialog(ShortcutCatalog.Build(owner)));
        dialog.UpdateLayout();

        // The shortcuts should be readable without scrolling at the main window's designed height.
        var designed = Show(PathHide.Tests.I18n.PopulatedMainWindow.Empty()).Height;
        Assert.True(dialog.DesiredSize.Height < designed, $"The dialog wants {dialog.DesiredSize.Height:0} px of {designed:0}.");
    }
}
