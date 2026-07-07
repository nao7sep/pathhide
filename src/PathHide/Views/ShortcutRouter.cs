using System.Windows.Input;

using PathHide.ViewModels;

namespace PathHide.Views;

/// <summary>
/// Routes a window-level <see cref="ShortcutAction"/> to the view-model command it runs, or marks it as
/// one the window dispatches itself (a picker or dialog). Pulled out of the window so a test can assert
/// every action is routed — the previous in-window switch's <c>default</c> arm let a newly-added action
/// silently no-op. The runtime guards stay in the window (a scan must be running for Cancel; Settings is
/// Windows-only): those read live view/VM state, not a static action-to-command map.
/// </summary>
public static class ShortcutRouter
{
    /// <summary>The command an action runs, or null when the window handles the action itself.</summary>
    public static ICommand? CommandFor(MainWindowViewModel vm, ShortcutAction action) => action switch
    {
        ShortcutAction.HideSelected => vm.HideSelectedCommand,
        ShortcutAction.ShowSelected => vm.ShowSelectedCommand,
        ShortcutAction.ReapplyAll => vm.ReapplyAllCommand,
        ShortcutAction.Reload => vm.ReloadCommand,
        ShortcutAction.CancelScan => vm.CancelScanCommand,
        _ => null,
    };

    /// <summary>
    /// True for actions the window dispatches itself because they open a file/folder picker or a dialog
    /// rather than running a view-model command.
    /// </summary>
    public static bool IsViewAction(ShortcutAction action) => action switch
    {
        ShortcutAction.AddFiles
            or ShortcutAction.AddDirectories
            or ShortcutAction.OpenSettings
            or ShortcutAction.ShowShortcuts => true,
        _ => false,
    };
}
