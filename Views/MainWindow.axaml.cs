using System.Linq;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        KeyDown += OnKeyDown;
        PathGrid.SelectionChanged += OnGridSelectionChanged;

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
        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            ViewModel.RemoveSelectedCommand.Execute(null);
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
}
