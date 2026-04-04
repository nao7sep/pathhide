using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using Serilog;

namespace PathHide.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MainWindowViewModel>();

    private readonly PathListStore _pathListStore = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly IVisibilityService _visibilityService;
    private readonly PathScanner _scanner;

    private List<PathEntry> _entries = [];
    private AppSettings _settings = new();
    private CancellationTokenSource? _scanCts;

    /// <summary>
    /// Set by the view to show a confirmation dialog. Returns true if confirmed.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public ObservableCollection<PathRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _scanTotal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _scanProgress;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBarText))]
    private string _notification = string.Empty;

    public string ProgressText => ScanTotal > 0
        ? $"Scanning {ScanProgress} / {ScanTotal}"
        : string.Empty;

    public string StatusBarText => !string.IsNullOrEmpty(Notification)
        ? Notification
        : BuildSummary();

    private string BuildSummary()
    {
        if (Rows.Count == 0)
            return "No entries — drop files or folders here to get started";

        var hidden = Rows.Count(r => r.ActualState == ActualState.Hidden);
        var visible = Rows.Count(r => r.ActualState == ActualState.Visible);
        var missing = Rows.Count(r => r.ActualState == ActualState.Missing);
        var problems = Rows.Count(r => r.ActualState is ActualState.Unreachable or ActualState.Error);

        var parts = new List<string> { $"{Rows.Count} entries" };
        if (hidden > 0) parts.Add($"{hidden} hidden");
        if (visible > 0) parts.Add($"{visible} visible");
        if (missing > 0) parts.Add($"{missing} missing");
        if (problems > 0) parts.Add($"{problems} problems");
        return string.Join("  ·  ", parts);
    }

    public MainWindowViewModel()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            _visibilityService = new WindowsVisibilityService(() => _settings.WindowsHideMode);
        else
            _visibilityService = new MacVisibilityService();

        _scanner = new PathScanner(_visibilityService);

        _settings = _settingsStore.Load();
        _entries = _pathListStore.Load();
        BuildRows();
        _ = RunScanAsync();
    }

    // --- Add / Remove ---

    public async void AddPaths(IEnumerable<string> paths)
    {
        var added = 0;
        var skipped = 0;
        var newEntries = new List<PathEntry>();

        foreach (var raw in paths)
        {
            if (!PathNormalizer.TryNormalize(raw, out var normalized, out _))
            {
                Log.Warning("Rejected path (not absolute): {Path}", raw);
                skipped++;
                continue;
            }

            if (_entries.Any(e => PathNormalizer.AreEqual(e.Path, normalized)))
            {
                skipped++;
                continue;
            }

            var entry = new PathEntry
            {
                Path = normalized,
                DesiredVisibility = DesiredVisibility.Hidden,
            };
            _entries.Add(entry);
            newEntries.Add(entry);
            added++;
        }

        if (added > 0)
        {
            if (!TrySavePaths())
                return;

            BuildRows();

            var newRows = Rows.Where(r => newEntries.Contains(r.Entry)).ToList();
            var summary = await ApplyDesiredStateAsync(newRows);
            ShowNotification($"{added} added, {skipped} skipped — {summary}");
        }
        else
        {
            ShowNotification($"{added} added, {skipped} skipped");
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var selected = Rows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        if (ConfirmAsync is not null)
        {
            var confirmed = await ConfirmAsync(
                "Remove entries",
                $"Remove {selected.Count} selected {(selected.Count == 1 ? "entry" : "entries")} from the list?");

            if (!confirmed)
                return;
        }

        foreach (var row in selected)
            _entries.Remove(row.Entry);

        if (!TrySavePaths())
            return;

        BuildRows();
        ShowNotification($"{selected.Count} removed");
    }

    // --- Hide / Show ---

    [RelayCommand]
    private async Task HideSelectedAsync()
    {
        var selected = Rows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        foreach (var row in selected)
        {
            row.Entry.DesiredVisibility = DesiredVisibility.Hidden;
            row.DesiredVisibility = DesiredVisibility.Hidden;
        }

        if (!TrySavePaths())
            return;

        var summary = await ApplyDesiredStateAsync(selected);
        ShowNotification(summary);
    }

    [RelayCommand]
    private async Task ShowSelectedAsync()
    {
        var selected = Rows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        foreach (var row in selected)
        {
            row.Entry.DesiredVisibility = DesiredVisibility.Shown;
            row.DesiredVisibility = DesiredVisibility.Shown;
        }

        if (!TrySavePaths())
            return;

        var summary = await ApplyDesiredStateAsync(selected);
        ShowNotification(summary);
    }

    [RelayCommand]
    private async Task HideAllAsync()
    {
        foreach (var row in Rows)
        {
            row.Entry.DesiredVisibility = DesiredVisibility.Hidden;
            row.DesiredVisibility = DesiredVisibility.Hidden;
        }

        if (!TrySavePaths())
            return;

        var summary = await ApplyDesiredStateAsync(Rows.ToList());
        ShowNotification(summary);
    }

    [RelayCommand]
    private async Task ShowAllAsync()
    {
        foreach (var row in Rows)
        {
            row.Entry.DesiredVisibility = DesiredVisibility.Shown;
            row.DesiredVisibility = DesiredVisibility.Shown;
        }

        if (!TrySavePaths())
            return;

        var summary = await ApplyDesiredStateAsync(Rows.ToList());
        ShowNotification(summary);
    }

    [RelayCommand]
    private async Task ReapplyAllAsync()
    {
        var summary = await ApplyDesiredStateAsync(Rows.ToList());
        ShowNotification(summary);
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        _settings = _settingsStore.Load();
        _entries = _pathListStore.Load();
        BuildRows();
        await RunScanAsync();
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
    }

    // --- Internals ---

    private bool TrySavePaths()
    {
        try
        {
            _pathListStore.Save(_entries);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save paths");
            ShowNotification($"Failed to save: {ex.Message}");
            return false;
        }
    }

    private void BuildRows()
    {
        Rows.Clear();
        foreach (var entry in _entries)
        {
            var row = new PathRowViewModel(entry);
            if (PathNormalizer.TryNormalize(entry.Path, out _, out var family))
                row.PathFamily = family;
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(StatusBarText));
    }

    private async Task RunScanAsync()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        IsScanning = true;
        ScanTotal = Rows.Count;
        ScanProgress = 0;

        var progress = new Progress<int>(p => ScanProgress = p);

        try
        {
            await foreach (var result in _scanner.ScanAsync(_entries, progress, token))
            {
                var row = Rows.FirstOrDefault(r => r.Entry == result.Entry);
                row?.ApplyScanResult(result.Inspection, result.Family);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("Scan cancelled");
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(StatusBarText));
        }
    }

    private async Task<string> ApplyDesiredStateAsync(List<PathRowViewModel> targets)
    {
        var applied = 0;
        var missing = 0;
        var errors = 0;

        foreach (var row in targets)
        {
            try
            {
                var inspection = await Task.Run(() => _visibilityService.Inspect(row.Path));

                if (inspection.ActualState == ActualState.Missing)
                {
                    missing++;
                    row.ActualState = ActualState.Missing;
                    continue;
                }

                if (inspection.ActualState is ActualState.Unreachable or ActualState.Error)
                {
                    errors++;
                    row.ActualState = inspection.ActualState;
                    continue;
                }

                await Task.Run(() =>
                {
                    if (row.Entry.DesiredVisibility == DesiredVisibility.Hidden)
                        _visibilityService.Hide(row.Path);
                    else
                        _visibilityService.Show(row.Path);
                });

                var updated = await Task.Run(() => _visibilityService.Inspect(row.Path));
                row.ApplyScanResult(updated, row.PathFamily);
                applied++;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to apply desired state to {Path}", row.Path);
                errors++;
                row.ActualState = ActualState.Error;
            }
        }

        var parts = new List<string>();
        if (applied > 0) parts.Add($"{applied} applied");
        if (missing > 0) parts.Add($"{missing} missing");
        if (errors > 0) parts.Add($"{errors} errors");
        return parts.Count > 0 ? string.Join(", ", parts) : "nothing to do";
    }

    private CancellationTokenSource? _notificationCts;

    private void ShowNotification(string message)
    {
        Log.Information("Notification: {Message}", message);
        Notification = message;

        _notificationCts?.Cancel();
        _notificationCts = new CancellationTokenSource();
        var token = _notificationCts.Token;

        _ = ClearNotificationAsync(token);
    }

    private async Task ClearNotificationAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(5000, token);
            Notification = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // Next notification replaced this one
        }
    }
}
