using System;
using CommunityToolkit.Mvvm.ComponentModel;
using PathHide.Models;

namespace PathHide.ViewModels;

public partial class PathRowViewModel : ObservableObject
{
    public PathEntry Entry { get; private set; }

    public string Path => Entry.Path;

    [ObservableProperty]
    private PathFamily _pathFamily;

    [ObservableProperty]
    private DesiredVisibility _desiredVisibility;

    [ObservableProperty]
    private ActualState _actualState = ActualState.Unknown;

    [ObservableProperty]
    private ItemKind _itemKind = ItemKind.Unknown;

    [ObservableProperty]
    private bool _isSelected;

    public PathRowViewModel(PathEntry entry)
    {
        Entry = entry;
        _desiredVisibility = entry.DesiredVisibility;
    }

    public void SyncEntry(PathEntry entry)
    {
        var previousPath = Entry.Path;
        Entry = entry;

        if (!string.Equals(previousPath, entry.Path, StringComparison.Ordinal))
            OnPropertyChanged(nameof(Path));

        DesiredVisibility = entry.DesiredVisibility;
    }

    public void ApplyScanResult(Services.PathInspection inspection, PathFamily family)
    {
        ActualState = inspection.ActualState;
        ItemKind = inspection.ItemKind;
        PathFamily = family;
    }
}
