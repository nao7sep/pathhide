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
    private Task _scanTask = Task.CompletedTask;
    private bool _suppressHideModeSave;

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

    [ObservableProperty]
    private bool _isHiddenAndSystem;

    // Currently, all settings are Windows-only. When a cross-platform setting is added,
    // change this to always return true and remove the platform check.
    public bool HasSettings { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

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
        var pending = Rows.Count(r => r.ActualState == ActualState.Unknown);
        var problems = Rows.Count(r => r.ActualState is ActualState.Unreachable or ActualState.Error);

        var parts = new List<string> { $"{Rows.Count} entries" };
        if (hidden > 0) parts.Add($"{hidden} hidden");
        if (visible > 0) parts.Add($"{visible} visible");
        if (missing > 0) parts.Add($"{missing} missing");
        if (pending > 0) parts.Add($"{pending} pending");
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
        SyncHideModeFromSettings();
        _entries = _pathListStore.Load();
        SyncRowsWithEntries();
        StartBackgroundScan();
    }

    // --- Add / Remove ---

    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var scanWasActive = await PauseScanningAsync();
        var added = 0;
        var skipped = 0;
        var addedPaths = new List<string>();
        var previousEntries = CloneEntries(_entries);

        try
        {
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

                _entries.Add(new PathEntry
                {
                    Path = normalized,
                    DesiredVisibility = DesiredVisibility.Hidden,
                });
                addedPaths.Add(normalized);
                added++;
            }

            if (added == 0)
            {
                ShowNotification($"{added} added, {skipped} skipped");
                return;
            }

            if (!TryCommitPathChanges(previousEntries))
                return;

            var newRows = SyncRowsWithEntries()
                .Where(r => addedPaths.Any(path => PathNormalizer.AreEqual(path, r.Path)))
                .ToList();
            var summary = await ApplyDesiredStateAsync(newRows);
            ShowNotification($"{added} added, {skipped} skipped — {summary}");
        }
        finally
        {
            ResumeScanningIfNeeded(scanWasActive);
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var scanWasActive = await PauseScanningAsync();
        var selected = Rows.Where(r => r.IsSelected).ToList();
        var previousEntries = CloneEntries(_entries);

        try
        {
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

            if (!TryCommitPathChanges(previousEntries))
                return;

            SyncRowsWithEntries();
            ShowNotification($"{selected.Count} removed");
        }
        finally
        {
            ResumeScanningIfNeeded(scanWasActive);
        }
    }

    // --- Hide / Show ---

    [RelayCommand]
    private async Task HideSelectedAsync()
    {
        var scanWasActive = await PauseScanningAsync();
        var selected = Rows.Where(r => r.IsSelected).ToList();
        var previousEntries = CloneEntries(_entries);

        try
        {
            if (selected.Count == 0)
                return;

            foreach (var row in selected)
            {
                row.Entry.DesiredVisibility = DesiredVisibility.Hidden;
                row.DesiredVisibility = DesiredVisibility.Hidden;
            }

            if (!TryCommitPathChanges(previousEntries))
                return;

            var summary = await ApplyDesiredStateAsync(selected);
            ShowNotification(summary);
        }
        finally
        {
            ResumeScanningIfNeeded(scanWasActive);
        }
    }

    [RelayCommand]
    private async Task ShowSelectedAsync()
    {
        var scanWasActive = await PauseScanningAsync();
        var selected = Rows.Where(r => r.IsSelected).ToList();
        var previousEntries = CloneEntries(_entries);

        try
        {
            if (selected.Count == 0)
                return;

            foreach (var row in selected)
            {
                row.Entry.DesiredVisibility = DesiredVisibility.Shown;
                row.DesiredVisibility = DesiredVisibility.Shown;
            }

            if (!TryCommitPathChanges(previousEntries))
                return;

            var summary = await ApplyDesiredStateAsync(selected);
            ShowNotification(summary);
        }
        finally
        {
            ResumeScanningIfNeeded(scanWasActive);
        }
    }

    [RelayCommand]
    private async Task HideAllAsync()
    {
        var scanWasActive = await PauseScanningAsync();
        var previousEntries = CloneEntries(_entries);

        try
        {
            foreach (var row in Rows)
            {
                row.Entry.DesiredVisibility = DesiredVisibility.Hidden;
                row.DesiredVisibility = DesiredVisibility.Hidden;
            }

            if (!TryCommitPathChanges(previousEntries))
                return;

            var summary = await ApplyDesiredStateAsync(Rows.ToList());
            ShowNotification(summary);
        }
        finally
        {
            ResumeScanningIfNeeded(scanWasActive);
        }
    }

    [RelayCommand]
    private async Task ShowAllAsync()
    {
        var scanWasActive = await PauseScanningAsync();
        var previousEntries = CloneEntries(_entries);

        try
        {
            foreach (var row in Rows)
            {
                row.Entry.DesiredVisibility = DesiredVisibility.Shown;
                row.DesiredVisibility = DesiredVisibility.Shown;
            }

            if (!TryCommitPathChanges(previousEntries))
                return;

            var summary = await ApplyDesiredStateAsync(Rows.ToList());
            ShowNotification(summary);
        }
        finally
        {
            ResumeScanningIfNeeded(scanWasActive);
        }
    }

    [RelayCommand]
    private async Task ReapplyAllAsync()
    {
        var scanWasActive = await PauseScanningAsync();
        try
        {
            var summary = await ApplyDesiredStateAsync(Rows.ToList());
            ShowNotification(summary);
        }
        finally
        {
            ResumeScanningIfNeeded(scanWasActive);
        }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        await PauseScanningAsync();
        _settings = _settingsStore.Load();
        SyncHideModeFromSettings();
        _entries = _pathListStore.Load();
        SyncRowsWithEntries();
        _scanTask = RunScanAsync();
        await _scanTask;
    }

    partial void OnIsHiddenAndSystemChanged(bool value)
    {
        if (_suppressHideModeSave)
            return;

        var previousMode = _settings.WindowsHideMode;
        _settings.WindowsHideMode = value
            ? WindowsHideMode.HiddenAndSystem
            : WindowsHideMode.HiddenOnly;

        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            _settings.WindowsHideMode = previousMode;
            _suppressHideModeSave = true;
            try
            {
                IsHiddenAndSystem = previousMode == WindowsHideMode.HiddenAndSystem;
            }
            finally
            {
                _suppressHideModeSave = false;
            }
            Log.Error(ex, "Failed to save settings");
            ShowNotification($"Failed to save settings: {ex.Message}");
        }
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

    private bool TryCommitPathChanges(List<PathEntry> previousEntries)
    {
        if (TrySavePaths())
            return true;

        _entries = previousEntries;
        SyncRowsWithEntries();
        return false;
    }

    private static List<PathEntry> CloneEntries(IEnumerable<PathEntry> entries)
    {
        return entries
            .Select(entry => new PathEntry
            {
                Path = entry.Path,
                DesiredVisibility = entry.DesiredVisibility,
            })
            .ToList();
    }

    private void StartBackgroundScan()
    {
        if (Rows.Count == 0)
            return;

        _scanTask = RunScanAsync();
    }

    private async Task<bool> PauseScanningAsync()
    {
        var scanCts = _scanCts;
        if (scanCts is null)
            return false;

        try
        {
            scanCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        if (!_scanTask.IsCompleted)
            await _scanTask;

        return true;
    }

    private void ResumeScanningIfNeeded(bool scanWasActive)
    {
        if (!scanWasActive || _scanCts is not null)
            return;

        StartBackgroundScan();
    }

    private void SyncHideModeFromSettings()
    {
        var isHiddenAndSystem = _settings.WindowsHideMode == WindowsHideMode.HiddenAndSystem;
        if (IsHiddenAndSystem == isHiddenAndSystem)
            return;

        _suppressHideModeSave = true;
        try
        {
            IsHiddenAndSystem = isHiddenAndSystem;
        }
        finally
        {
            _suppressHideModeSave = false;
        }
    }

    private List<PathRowViewModel> SyncRowsWithEntries()
    {
        var remainingRows = Rows.ToList();

        var desiredRows = new List<PathRowViewModel>(_entries.Count);
        var addedRows = new List<PathRowViewModel>();

        foreach (var entry in _entries)
        {
            var existingIndex = remainingRows.FindIndex(row => PathNormalizer.AreEqual(row.Path, entry.Path));
            PathRowViewModel row;
            if (existingIndex < 0)
            {
                row = new PathRowViewModel(entry);
                addedRows.Add(row);
            }
            else
            {
                row = remainingRows[existingIndex];
                remainingRows.RemoveAt(existingIndex);
                row.SyncEntry(entry);
            }

            if (PathNormalizer.TryNormalize(entry.Path, out _, out var family))
                row.PathFamily = family;
            else
                row.PathFamily = default;

            desiredRows.Add(row);
        }

        var desiredSet = new HashSet<PathRowViewModel>(desiredRows);
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(Rows[i]))
                Rows.RemoveAt(i);
        }

        for (var i = 0; i < desiredRows.Count; i++)
        {
            var desiredRow = desiredRows[i];
            if (i < Rows.Count && ReferenceEquals(Rows[i], desiredRow))
                continue;

            var existingIndex = Rows.IndexOf(desiredRow);
            if (existingIndex >= 0)
                Rows.Move(existingIndex, i);
            else
                Rows.Insert(i, desiredRow);
        }

        OnPropertyChanged(nameof(StatusBarText));
        return addedRows;
    }

    private async Task RunScanAsync()
    {
        var previousScanCts = _scanCts;
        previousScanCts?.Cancel();
        previousScanCts?.Dispose();

        var scanCts = new CancellationTokenSource();
        _scanCts = scanCts;
        var token = scanCts.Token;
        var entries = _entries.ToList();

        IsScanning = true;
        ScanTotal = entries.Count;
        ScanProgress = 0;

        var progress = new Progress<int>(p =>
        {
            if (ReferenceEquals(_scanCts, scanCts))
                ScanProgress = p;
        });

        try
        {
            await foreach (var result in _scanner.ScanAsync(entries, progress, token))
            {
                if (!ReferenceEquals(_scanCts, scanCts))
                    return;

                var row = Rows.FirstOrDefault(r => r.Entry == result.Entry);
                row?.ApplyScanResult(result.Inspection, result.Family);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("Scan cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Scan failed");
            ShowNotification($"Scan failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_scanCts, scanCts))
            {
                _scanCts = null;
                IsScanning = false;
                OnPropertyChanged(nameof(StatusBarText));
            }

            scanCts.Dispose();
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
