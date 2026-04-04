using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using PathHide.ViewModels;

namespace PathHide.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();

        AddFilesButton.Click += OnAddFilesClick;
        AddFoldersButton.Click += OnAddFoldersClick;

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        KeyDown += OnKeyDown;
        PathGrid.SelectionChanged += OnGridSelectionChanged;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ViewModel.ConfirmAsync = ShowConfirmAsync;
    }

    private async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var box = MessageBoxManager.GetMessageBoxStandard(
            title, message, ButtonEnum.YesNo);
        var result = await box.ShowWindowDialogAsync(this);
        return result == ButtonResult.Yes;
    }

    private async void OnAddFilesClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Files",
            AllowMultiple = true,
        });

        if (files.Count > 0)
            ViewModel.AddPaths(files.Select(f => f.Path.LocalPath));
    }

    private async void OnAddFoldersClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add Folders",
            AllowMultiple = true,
        });

        if (folders.Count > 0)
            ViewModel.AddPaths(folders.Select(f => f.Path.LocalPath));
    }

#pragma warning disable CS0618 // DragDrop API transition — suppress until Avalonia stabilizes new API
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var items = e.Data.GetFiles();
        if (items is null)
            return;

        var paths = items.Select(i => i.Path.LocalPath);
        ViewModel.AddPaths(paths);
    }
#pragma warning restore CS0618

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
        }
    }
}
