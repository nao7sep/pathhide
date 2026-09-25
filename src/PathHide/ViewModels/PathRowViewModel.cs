using System;
using CommunityToolkit.Mvvm.ComponentModel;
using PathHide.I18n;
using PathHide.Models;

namespace PathHide.ViewModels;

public partial class PathRowViewModel : ObservableObject
{
    public PathEntry Entry { get; private set; }

    public string Path => Entry.Path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PathFamilyText))]
    private PathFamily _pathFamily;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DesiredVisibilityText))]
    private DesiredVisibility _desiredVisibility;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualStateText))]
    private ActualState _actualState = ActualState.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ItemKindText))]
    private ItemKind _itemKind = ItemKind.Unknown;

    [ObservableProperty]
    private bool _isSelected;

    // The words each column shows. The grid sorts by the values above, not by these, so a column
    // keeps the order it has always had whatever language is showing.
    public string PathFamilyText => Localizer.T(PathFamily switch
    {
        PathFamily.Posix => "family.posix",
        PathFamily.Windows => "family.windows",
        PathFamily.Unc => "family.unc",
        _ => throw new ArgumentOutOfRangeException(nameof(PathFamily), PathFamily, null),
    });

    public string DesiredVisibilityText => Localizer.T(DesiredVisibility switch
    {
        DesiredVisibility.Hidden => "desired.hidden",
        DesiredVisibility.Shown => "desired.shown",
        _ => throw new ArgumentOutOfRangeException(nameof(DesiredVisibility), DesiredVisibility, null),
    });

    public string ActualStateText => Localizer.T(ActualState switch
    {
        ActualState.Hidden => "actual.hidden",
        ActualState.Visible => "actual.visible",
        ActualState.Missing => "actual.missing",
        ActualState.AccessDenied => "actual.accessDenied",
        ActualState.Error => "actual.error",
        ActualState.Unknown => "actual.unknown",
        ActualState.Unresponsive => "actual.unresponsive",
        _ => throw new ArgumentOutOfRangeException(nameof(ActualState), ActualState, null),
    });

    public string ItemKindText => Localizer.T(ItemKind switch
    {
        ItemKind.File => "kind.file",
        ItemKind.Directory => "kind.directory",
        ItemKind.Symlink => "kind.symlink",
        ItemKind.Other => "kind.other",
        ItemKind.Unknown => "kind.unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(ItemKind), ItemKind, null),
    });

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

    /// <summary>Called by the window's view model when the language changes.</summary>
    internal void Retranslate()
    {
        OnPropertyChanged(nameof(PathFamilyText));
        OnPropertyChanged(nameof(DesiredVisibilityText));
        OnPropertyChanged(nameof(ActualStateText));
        OnPropertyChanged(nameof(ItemKindText));
    }
}
