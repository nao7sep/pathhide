using System.Linq;
using System.ComponentModel;
using System.Threading.Tasks;
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
    // Cmd/Ctrl+, → Settings (the conventions' standard settings accelerator). Settings is
    // Windows-only here, so Control is the right modifier; the gesture is defined once and
    // used both to label the menu item and to match the key press in OnKeyDown. A MenuFlyout
    // item's own HotKey only registers while the flyout is open, so the accelerator is wired
    // at the window level instead, with InputGesture providing the visible association.
    private static readonly KeyGesture SettingsGesture = new(Key.OemComma, KeyModifiers.Control);

    // Ctrl+/ opens the keyboard-shortcuts help (cross-platform — the convention's
    // Cmd/Ctrl+/). Also reachable from the menu, which shows this accelerator.
    private static readonly KeyGesture ShortcutsGesture = new(Key.OemQuestion, KeyModifiers.Control);

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();

        AddFilesButton.Click += OnAddFilesClick;
        AddFoldersButton.Click += OnAddFoldersClick;

        OpenLogMenuItem.Click += OnOpenLogClick;
        SettingsMenuItem.Click += OnSettingsClick;
        SettingsMenuItem.InputGesture = SettingsGesture;
        AboutMenuItem.Click += OnAboutClick;
        ShortcutsMenuItem.Click += OnShortcutsClick;
        ShortcutsMenuItem.InputGesture = ShortcutsGesture;

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

    private Task ShowShortcutsAsync() => new ShortcutsDialog().ShowDialog(this);

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

    private async void OnAddFilesClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Files",
            AllowMultiple = true,
        });

        if (files.Count > 0)
            await ViewModel.AddPathsAsync(files.Select(f => f.Path.LocalPath));
    }

    private async void OnAddFoldersClick(object? sender, RoutedEventArgs e)
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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+/ opens shortcuts help — cross-platform, always available.
        if (ShortcutsGesture.Matches(e))
        {
            e.Handled = true;
            _ = ShowShortcutsAsync();
            return;
        }

        // Gated on HasSettings so the accelerator is live only where Settings exists
        // (Windows); a modal dialog is a separate top-level, so this never fires while
        // Settings is already open.
        if (ViewModel.HasSettings && SettingsGesture.Matches(e))
        {
            e.Handled = true;
            _ = OpenSettingsAsync();
        }
    }

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
            ViewModel.RemoveSelectedCommand.Execute(null);
        }
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
