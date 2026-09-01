using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PathHide.Services;
using PathHide.ViewModels;

namespace PathHide.Views;

public partial class MainWindow : Window
{
    // The single source of truth for the window's accelerators and the help modal. Built once in
    // OnLoaded — where the platform command key (Cmd on macOS, Ctrl on Windows) and the view model
    // are both available — so a label can never describe a binding that does not exist. A MenuFlyout
    // item's own HotKey only registers while the flyout is open, so accelerators are matched at the
    // window level in OnKeyDown, with InputGesture providing the visible menu association.
    private IReadOnlyList<ShortcutItem> _shortcuts = [];

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();

        if (OperatingSystem.IsWindows())
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://PathHide/Assets/icon-win.png"));
            Icon = new WindowIcon(iconStream);
        }

        AddFilesButton.Click += OnAddFilesClick;
        AddFoldersButton.Click += OnAddFoldersClick;
        RemoveButton.Click += OnRemoveClick;

        OpenLogMenuItem.Header = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Show Log File in Explorer"
            : "Show Log File in Finder";
        OpenLogMenuItem.Click += OnOpenLogClick;
        SettingsMenuItem.Click += OnSettingsClick;
        AboutMenuItem.Click += OnAboutClick;
        ShortcutsMenuItem.Click += OnShortcutsClick;

        PathListReceiver.AddHandler(DragDrop.DropEvent, OnDrop);
        PathListReceiver.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        PathListReceiver.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        Deactivated += (_, _) => SetPathDropActive(false);
        Closed += (_, _) => SetPathDropActive(false);
        KeyDown += OnKeyDown;
        PathGrid.SelectionChanged += OnGridSelectionChanged;
        PathGrid.KeyDown += OnGridKeyDown;
        ActionButtons.KeyDown += OnActionButtonsKeyDown;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyWindowMinimums();

        // Build the catalog now that PlatformSettings (the platform command key) and the view model
        // are both available, then point the accelerator-bearing menu items at the live gestures so
        // their visible hint always matches what OnKeyDown actually binds.
        _shortcuts = ShortcutCatalog.Build(this);
        SettingsMenuItem.InputGesture = GestureFor(ShortcutAction.OpenSettings);
        ShortcutsMenuItem.InputGesture = GestureFor(ShortcutAction.ShowShortcuts);

        ViewModel.ConfirmDestructiveAsync = request =>
            ConfirmDialog.ConfirmDestructiveAsync(this, request.Title, request.Message, request.ConfirmLabel);
        ViewModel.ShowNoticeAsync = (title, body) => NoticeDialog.ShowAsync(this, title, body);
        ViewModel.Initialize();
        PathGrid.Columns.First(c => c.SortMemberPath == nameof(PathRowViewModel.Path))
            .Sort(ListSortDirection.Ascending);
        Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel.Rows.Count > 0)
                PathGrid.Focus();
            else
                AddFilesButton.Focus();
        });
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        await new AboutDialog().ShowDialog(this);
    }

    private async void OnShortcutsClick(object? sender, RoutedEventArgs e) => await ShowShortcutsAsync();

    /// <summary>
    /// Derives the window minimum from the live layout: the grid's column minimums plus the
    /// measured chrome.
    /// </summary>
    /// <remarks>
    /// Never a hand-typed constant, so adding or resizing a column moves the minimum with it and
    /// the window can never shrink small enough to hide the toolbar, list, or status bar.
    /// <para>Re-run whenever the UI font changes. The font is user-changeable at runtime and
    /// applies live through a DynamicResource, so a minimum measured once at load went stale:
    /// switch to a wider family and the toolbar's natural width exceeded it, letting the window
    /// be dragged down until the rightmost action buttons clipped under the hamburger — the very
    /// truncation the minimum exists to prevent. Both chrome heights are measured for the same
    /// reason, rather than the pixel constants they used to be.</para>
    /// </remarks>
    private void ApplyWindowMinimums()
    {
        Toolbar.Measure(Size.Infinity);
        StatusBar.Measure(Size.Infinity);

        MinWidth = Math.Max(
            WindowMetrics.MinWidthFor(PathGrid.Columns.Select(c => c.MinWidth)),
            Toolbar.DesiredSize.Width);
        MinHeight = WindowMetrics.MinHeightFor(
            Toolbar.DesiredSize.Height,
            StatusBar.DesiredSize.Height);
    }

    private Task ShowShortcutsAsync() => new ShortcutsDialog(_shortcuts).ShowDialog(this);

    private void OnOpenLogClick(object? sender, RoutedEventArgs e)
    {
        LogReveal.Reveal();
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e) => await OpenSettingsAsync();

    private async Task OpenSettingsAsync()
    {
        var dialog = new SettingsDialog(
            ViewModel.UiFontFamily,
            ViewModel.IsHiddenAndSystem,
            ViewModel.HasWindowsHideMode,
            ViewModel.TryApplySettings);
        await dialog.ShowDialog(this);

        if (dialog.Accepted)
        {
            // The font applies live through a DynamicResource, so the chrome's natural size
            // changes with it. Re-derive after the layout pass has taken the new family.
            Dispatcher.UIThread.Post(ApplyWindowMinimums, DispatcherPriority.Loaded);
        }
    }

    private async void OnAddFilesClick(object? sender, RoutedEventArgs e) => await AddFilesAsync();

    private async Task AddFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Files",
            AllowMultiple = true,
        });

        if (files.Count > 0)
            await ViewModel.AddPathsCommand.ExecuteAsync(files.Select(f => f.Path.LocalPath));
    }

    private async void OnAddFoldersClick(object? sender, RoutedEventArgs e) => await AddFoldersAsync();

    private async Task AddFoldersAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add Directories",
            AllowMultiple = true,
        });

        if (folders.Count > 0)
            await ViewModel.AddPathsCommand.ExecuteAsync(folders.Select(f => f.Path.LocalPath));
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var acceptsFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = acceptsFiles ? DragDropEffects.Copy : DragDropEffects.None;
        SetPathDropActive(acceptsFiles);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => SetPathDropActive(false);

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        SetPathDropActive(false);
        var items = e.DataTransfer.TryGetFiles();
        if (items is null)
            return;

        var deliveredItems = items.ToArray();
        var paths = deliveredItems
            .Select(i => i.Path.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        await ViewModel.AddDroppedPathsAsync(paths, deliveredItems.Length - paths.Length);
    }

    private void SetPathDropActive(bool active) => PathListReceiver.Classes.Set("dropActive", active);

    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        foreach (PathRowViewModel row in e.RemovedItems)
            row.IsSelected = false;

        foreach (PathRowViewModel row in e.AddedItems)
            row.IsSelected = true;
    }

    // The window's command layer (per the composite-control conventions): it owns the application
    // accelerators and reads the current selection through the view-model commands. It deliberately
    // does NOT own list navigation (Up/Down) or action-button traversal (Left/Right) — those stay with
    // their controls below. A modal dialog is a separate top-level, so none of these fire while a
    // dialog is open.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        foreach (var item in _shortcuts)
        {
            if (item.Gesture is { } gesture && item.Action is { } action && gesture.Matches(e))
            {
                // Only mark handled when the action actually ran, so a gesture whose command is
                // unavailable (e.g. Esc while not scanning) leaves the key to its default handling.
                if (TryRunShortcut(action))
                    e.Handled = true;
                return;
            }
        }
    }

    private bool TryRunShortcut(ShortcutAction action)
    {
        if (ShortcutRouter.IsViewAction(action))
            return RunViewAction(action);

        // Esc cancels only while a scan is running; otherwise it stays unhandled.
        if (action == ShortcutAction.CancelScan && !ViewModel.IsScanning)
            return false;

        var command = ShortcutRouter.CommandFor(ViewModel, action);
        return command is not null && TryExecute(command);
    }

    // The actions the window dispatches itself: each opens a file/folder picker or a dialog rather
    // than running a view-model command.
    private bool RunViewAction(ShortcutAction action) => action switch
    {
        ShortcutAction.AddFiles => Run(AddFilesAsync),
        ShortcutAction.AddDirectories => Run(AddFoldersAsync),
        ShortcutAction.OpenSettings => Run(OpenSettingsAsync),
        ShortcutAction.ShowShortcuts => Run(ShowShortcutsAsync),
        _ => false,
    };

    // Fires an async window action (a picker or dialog) and reports the gesture as handled. The task
    // is intentionally not awaited — the key handler is synchronous and the action runs to completion
    // on the UI thread on its own.
    private static bool Run(Func<Task> action)
    {
        _ = action();
        return true;
    }

    private static bool TryExecute(ICommand command)
    {
        if (!command.CanExecute(null))
            return false;

        command.Execute(null);
        return true;
    }

    private KeyGesture? GestureFor(ShortcutAction action) =>
        _shortcuts.FirstOrDefault(i => i.Action == action)?.Gesture;

    // Delete removes the selected entries — but only while the list itself has focus. It is
    // wired on the grid, not the window, so the destructive command can never fire from a
    // toolbar button or other focused control. Backspace is deliberately NOT a delete alias:
    // on a focused control it reads as "go back"/erase, so triggering a destructive remove
    // from it is a footgun.
    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            _ = RemoveSelectedWithRecoveryAsync();
        }
    }

    private async void OnRemoveClick(object? sender, RoutedEventArgs e) => await RemoveSelectedWithRecoveryAsync();

    // Both the Remove button and the Delete key route here so the grid recovers a usable
    // selection after a removal. Without it, deleting the selected rows leaves nothing
    // selected and the keyboard dead-ends — no anchor for the next Delete or an arrow. We
    // note the lowest selected row first, run the removal, then — only if everything that was
    // selected got removed — select the row that slid into that slot (clamped to the last
    // row) and return focus to the grid so the keyboard stays live.
    private async Task RemoveSelectedWithRecoveryAsync()
    {
        // In the grid's VISIBLE order — the recovered index is applied as a grid
        // index, and the grid is sorted from startup and re-sortable on five
        // columns, so the model's insertion order names different rows.
        var viewOrder = PathGrid.CollectionView?.OfType<PathRowViewModel>().ToList()
            ?? ViewModel.Rows.ToList();
        var anchor = SelectionRecovery.Anchor(
            viewOrder,
            PathGrid.SelectedItems.OfType<PathRowViewModel>());
        await ViewModel.RemoveSelectedCommand.ExecuteAsync(null);

        // Nothing removed (e.g. the confirm was cancelled, or a selection still stands), or
        // the list is now empty — no recovery to do.
        if (ViewModel.Rows.Count == 0 || PathGrid.SelectedIndex >= 0)
            return;

        var target = SelectionRecovery.TargetIndex(anchor, ViewModel.Rows.Count);
        // Defer past the grid's own handling of the collection change (which clears the
        // selection), so this set is the last word — matching OnLoaded's focus pattern.
        Dispatcher.UIThread.Post(() =>
        {
            if (target >= 0 && target < ViewModel.Rows.Count)
            {
                PathGrid.SelectedIndex = target;
                PathGrid.Focus();
            }
        });
    }

    // The action bar is ONE tab stop (KeyboardNavigation.TabNavigation="Once" in the XAML),
    // and these keys move within it: Left/Right to the adjacent button, Home/End to the ends,
    // skipping any currently hidden (Cancel, shown only while scanning) and stopping at the
    // ends rather than letting the key escape the group. Ten separately-tabbable buttons made
    // reaching the grid below cost up to ten Tab presses — and the bar's width is a user
    // preference, so it grew with every configured action.
    private void OnActionButtonsKeyDown(object? sender, KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.Left => ToolbarKey.Previous,
            Key.Right => ToolbarKey.Next,
            Key.Home => ToolbarKey.Home,
            Key.End => ToolbarKey.End,
            _ => (ToolbarKey?)null,
        };
        if (key is null)
            return;

        var buttons = ActionButtons.GetLogicalDescendants()
            .OfType<Button>()
            .Where(b => b.IsVisible && b.IsEffectivelyEnabled && b.Focusable)
            .ToList();

        var current = buttons.FindIndex(b => b.IsFocused);
        if (current < 0)
            return;

        e.Handled = true;
        if (ActionButtonNavigation.Target(key.Value, current, buttons.Count) is { } next)
            buttons[next].Focus(NavigationMethod.Directional);
    }
}
