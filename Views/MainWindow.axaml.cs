using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
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

        AddFilesButton.Click += OnAddFilesClick;
        AddFoldersButton.Click += OnAddFoldersClick;
        RemoveButton.Click += OnRemoveClick;

        OpenLogMenuItem.Click += OnOpenLogClick;
        SettingsMenuItem.Click += OnSettingsClick;
        AboutMenuItem.Click += OnAboutClick;
        ShortcutsMenuItem.Click += OnShortcutsClick;

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        KeyDown += OnKeyDown;
        PathGrid.SelectionChanged += OnGridSelectionChanged;
        PathGrid.KeyDown += OnGridKeyDown;
        ActionButtons.KeyDown += OnActionButtonsKeyDown;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Build the catalog now that PlatformSettings (the platform command key) and the view model
        // are both available, then point the accelerator-bearing menu items at the live gestures so
        // their visible hint always matches what OnKeyDown actually binds.
        _shortcuts = ShortcutCatalog.Build(this, ViewModel.HasSettings);
        SettingsMenuItem.InputGesture = GestureFor(ShortcutAction.OpenSettings);
        ShortcutsMenuItem.InputGesture = GestureFor(ShortcutAction.ShowShortcuts);

        ViewModel.ConfirmDestructiveAsync = request =>
            ConfirmDialog.ConfirmDestructiveAsync(this, request.Title, request.Message, request.ConfirmLabel);
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

    private Task ShowShortcutsAsync() => new ShortcutsDialog(_shortcuts).ShowDialog(this);

    private void OnOpenLogClick(object? sender, RoutedEventArgs e)
    {
        LogReveal.Reveal();
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e) => await OpenSettingsAsync();

    private async Task OpenSettingsAsync()
    {
        var dialog = new SettingsDialog(ViewModel.IsHiddenAndSystem);
        await dialog.ShowDialog(this);

        if (dialog.Accepted)
            ViewModel.SetWindowsHideMode(dialog.IsHiddenAndSystem);
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
            await ViewModel.AddPathsAsync(files.Select(f => f.Path.LocalPath));
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
            await ViewModel.AddPathsAsync(folders.Select(f => f.Path.LocalPath));
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles();
        if (items is null)
            return;

        var paths = items.Select(i => i.Path.LocalPath);
        await ViewModel.AddPathsAsync(paths);
    }

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

    private bool TryRunShortcut(ShortcutAction action) => action switch
    {
        ShortcutAction.AddFiles => Run(AddFilesAsync),
        ShortcutAction.AddDirectories => Run(AddFoldersAsync),
        ShortcutAction.HideSelected => TryExecute(ViewModel.HideSelectedCommand),
        ShortcutAction.ShowSelected => TryExecute(ViewModel.ShowSelectedCommand),
        ShortcutAction.ReapplyAll => TryExecute(ViewModel.ReapplyAllCommand),
        ShortcutAction.Reload => TryExecute(ViewModel.ReloadCommand),
        // Esc cancels only while a scan is running; otherwise it stays unhandled.
        ShortcutAction.CancelScan => ViewModel.IsScanning && TryExecute(ViewModel.CancelScanCommand),
        // Defensive: the catalog already omits this row off Windows, so it cannot be reached there.
        ShortcutAction.OpenSettings => ViewModel.HasSettings && Run(OpenSettingsAsync),
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
        var anchor = LowestSelectedRowIndex();
        await ViewModel.RemoveSelectedCommand.ExecuteAsync(null);

        // Nothing removed (e.g. the confirm was cancelled, or a selection still stands), or
        // the list is now empty — no recovery to do.
        if (ViewModel.Rows.Count == 0 || PathGrid.SelectedIndex >= 0)
            return;

        var target = Math.Clamp(anchor, 0, ViewModel.Rows.Count - 1);
        // Defer past the grid's own handling of the collection change (which clears the
        // selection), so this set is the last word — matching OnLoaded's focus pattern.
        Dispatcher.UIThread.Post(() =>
        {
            if (target < ViewModel.Rows.Count)
            {
                PathGrid.SelectedIndex = target;
                PathGrid.Focus();
            }
        });
    }

    private int LowestSelectedRowIndex()
    {
        var lowest = int.MaxValue;
        foreach (var item in PathGrid.SelectedItems)
        {
            if (item is PathRowViewModel row)
            {
                var index = ViewModel.Rows.IndexOf(row);
                if (index >= 0 && index < lowest)
                    lowest = index;
            }
        }
        return lowest == int.MaxValue ? 0 : lowest;
    }

    // The action buttons are independent, individually Tab-reachable controls. As a keyboard
    // convenience, Left/Right also move focus to the adjacent action button — skipping any
    // that are currently hidden (e.g. Cancel, shown only while scanning) and stopping at the
    // ends rather than letting the arrow escape the group.
    private void OnActionButtonsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right))
            return;

        var buttons = ActionButtons.GetLogicalDescendants()
            .OfType<Button>()
            .Where(b => b.IsVisible && b.IsEffectivelyEnabled && b.Focusable)
            .ToList();

        var current = buttons.FindIndex(b => b.IsFocused);
        if (current < 0)
            return;

        e.Handled = true;
        var next = current + (e.Key == Key.Right ? 1 : -1);
        if (next >= 0 && next < buttons.Count)
            buttons[next].Focus(NavigationMethod.Directional);
    }
}
