using CommunityToolkit.Mvvm.ComponentModel;
using PathHide.Models;

namespace PathHide.ViewModels;

public partial class PathRowViewModel : ObservableObject
{
    public PathEntry Entry { get; }

    public string Path => Entry.Path;

    [ObservableProperty]
    private PathFamily _pathFamily;

    [ObservableProperty]
    private DesiredVisibility _desiredVisibility;

    [ObservableProperty]
    private ActualState _actualState;

    [ObservableProperty]
    private ItemKind _itemKind;

    [ObservableProperty]
    private bool _isSelected;

    public PathRowViewModel(PathEntry entry)
    {
        Entry = entry;
        _desiredVisibility = entry.DesiredVisibility;
    }

    public void ApplyScanResult(Services.PathInspection inspection, PathFamily family)
    {
        ActualState = inspection.ActualState;
        ItemKind = inspection.ItemKind;
        PathFamily = family;
    }
}
