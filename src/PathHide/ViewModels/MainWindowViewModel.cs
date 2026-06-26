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

namespace PathHide.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IJsonStore<List<PathEntry>> _pathListStore;
    private readonly IJsonStore<AppSettings> _settingsStore;
    private readonly IVisibilityService _visibilityService;
    private readonly PathScanner _scanner;

    // The same AppSettings instance the Windows visibility service closes over (wired in
    // App's composition root). Mutate its fields in place; never reassign the reference,
    // or the service would read stale state.
    private readonly AppSettings _settings;

    private List<PathEntry> _entries = [];
    private CancellationTokenSource? _scanCts;
    private Task _scanTask = Task.CompletedTask;
    private bool _initialized;

    /// <summary>
    /// Set by the view to show a destructive-action confirmation dialog. Returns true if the
    /// user confirms. Left null in headless contexts (tests), where the destructive action
    /// proceeds unprompted.
    /// </summary>
    public Func<ConfirmRequest, Task<bool>>? ConfirmDestructiveAsync { get; set; }

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

    // Currently, all settings are Windows-only. When a cross-platform setting is added,
    // change this to always return true and remove the platform check.
    public bool HasSettings { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Current Windows hide mode as a bool, used to seed the settings dialog. Read-only:
    /// the mode is changed and persisted through <see cref="SetWindowsHideMode"/>, never
    /// through a bound setter, so there is no save side effect on assignment.
    /// </summary>
    public bool IsHiddenAndSystem => _settings.WindowsHideMode == WindowsHideMode.HiddenAndSystem;

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
        var problems = Rows.Count(r => r.ActualState is ActualState.AccessDenied or ActualState.Error);

        var parts = new List<string> { $"{Rows.Count} entries" };
        if (hidden > 0) parts.Add($"{hidden} hidden");
        if (visible > 0) parts.Add($"{visible} visible");
        if (missing > 0) parts.Add($"{missing} missing");
        if (pending > 0) parts.Add($"{pending} pending");
        if (problems > 0) parts.Add($"{problems} problems");
        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// All dependencies are supplied by the composition root (see <c>App</c>),
    /// including the already-loaded <paramref name="settings"/>. The Windows
    /// visibility service closes over that same instance to read the current hide
    /// mode, so the view model mutates it in place rather than replacing it.
    /// </summary>
    /// <remarks>
    /// Construction is side-effect-free: no disk I/O and no scan happen here, so the
    /// type is safe to instantiate outside a running app. Call <see cref="Initialize"/>
    /// once the view is ready to load entries and start scanning.
    /// </remarks>
    public MainWindowViewModel(
        IVisibilityService visibilityService,
        IJsonStore<List<PathEntry>> pathListStore,
        IJsonStore<AppSettings> settingsStore,
        AppSettings settings)
    {
        _visibilityService = visibilityService;
        _pathListStore = pathListStore;
        _settingsStore = settingsStore;
        _settings = settings;
        _scanner = new PathScanner(visibilityService);
    }

    /// <summary>
    /// Loads persisted path entries and starts the initial background scan. The view
    /// calls this once it is loaded. Idempotent — only the first call has any effect,
    /// so a repeated Loaded event cannot trigger a second load or scan.
    /// </summary>
    public void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;

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
                    Log.Warn("add: rejected non-absolute path", new { path = raw });
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

            Log.Info("add paths", new { added, skipped });

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

            if (ConfirmDestructiveAsync is not null)
            {
                var confirmed = await ConfirmDestructiveAsync(new ConfirmRequest(
                    "Remove entries",
                    $"Remove {selected.Count} selected {(selected.Count == 1 ? "entry" : "entries")} from the list?",
                    "Remove"));

                if (!confirmed)
                    return;
            }

            foreach (var row in selected)
                _entries.Remove(row.Entry);

            Log.Info("remove paths", new { removed = selected.Count });

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

        // Reload reconciles the path list and re-scans. It deliberately does NOT reload
        // settings: the hide mode is owned in-app and persisted immediately on every change
        // (see SetWindowsHideMode), so the in-memory value never diverges from disk. Copying
        // a freshly loaded settings object back into the shared instance field-by-field would
        // be both brittle (it silently couples to AppSettings having one field) and pointless.
        Log.Info("reload");
        _entries = _pathListStore.Load();
        SyncRowsWithEntries();
        _scanTask = RunScanAsync();
        await _scanTask;
    }

    /// <summary>
    /// Updates and persists the Windows hide mode. On a save failure the in-memory
    /// mode is restored to its previous value — so it never diverges from disk or from
    /// what the visibility service reads — and the failure is surfaced to the user.
    /// No-op when the mode is unchanged.
    /// </summary>
    public void SetWindowsHideMode(bool hiddenAndSystem)
    {
        var newMode = hiddenAndSystem ? WindowsHideMode.HiddenAndSystem : WindowsHideMode.HiddenOnly;
        if (_settings.WindowsHideMode == newMode)
            return;

        var previousMode = _settings.WindowsHideMode;
        _settings.WindowsHideMode = newMode;

        try
        {
            _settingsStore.Save(_settings);
            Log.Info("settings: hide mode changed", new { mode = newMode });
            OnPropertyChanged(nameof(IsHiddenAndSystem));
        }
        catch (Exception ex)
        {
            _settings.WindowsHideMode = previousMode;
            Log.Error("settings: save failed", ex);
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
            // Sort a snapshot so paths.json is diff-stable without mutating the
            // live in-memory list. UI ordering is a separate concern handled by
            // the DataGrid's own sort.
            var snapshot = _entries
                .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _pathListStore.Save(snapshot);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("paths: save failed", ex);
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
            Log.Info("scan: cancelled");
        }
        catch (Exception ex)
        {
            Log.Error("scan: failed", ex);
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
        Log.Info("apply: start", new { count = targets.Count });

        var applied = 0;
        var missing = 0;
        var errors = 0;
        var retryBucket = new List<PathRowViewModel>();

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

                // Access-denied at inspect time is the same recoverable condition as a
                // denied Hide/Show write: on Windows a single elevated retry (drained below)
                // may have the rights to read and change it, so it joins that bucket rather
                // than the write attempt, which would only re-hit the same denial. The
                // platform gate matches the bucket-drain guard below — off Windows there is
                // no elevation step, so AccessDenied stays a terminal error alongside Error,
                // which no elevation can fix. A genuinely absent path is Missing (handled
                // above), never AccessDenied, so this never forces a futile elevation prompt.
                if (inspection.ActualState == ActualState.AccessDenied
                    && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    retryBucket.Add(row);
                    continue;
                }

                if (inspection.ActualState is ActualState.AccessDenied or ActualState.Error)
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
            catch (UnauthorizedAccessException) when (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Access-denied on Windows is recoverable via a single elevated retry
                // (below). The filter keeps this Windows-only; on other platforms the
                // general handler counts it as a plain error — no elevation path exists.
                retryBucket.Add(row);
            }
            catch (Exception ex)
            {
                Log.Error("apply: failed", ex, new { path = row.Path });
                errors++;
                var recheck = await Task.Run(() => _visibilityService.Inspect(row.Path));
                row.ApplyScanResult(recheck, row.PathFamily);
            }
        }

        int? elevationExitCode = null;

        // retryBucket is only ever populated on Windows (the catch above is filtered to
        // Windows), so this platform check is logically redundant — but it is REQUIRED, not
        // documentary: it is the guard the CA1416 analyzer needs to permit the
        // [SupportedOSPlatform("windows")] call to ApplyAsync below. Do not remove it.
        if (retryBucket.Count > 0 && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var toHide = retryBucket
                .Where(r => r.Entry.DesiredVisibility == DesiredVisibility.Hidden
                         && _settings.WindowsHideMode == WindowsHideMode.HiddenOnly)
                .Select(r => r.Path)
                .ToList();

            var toHideWithSystem = retryBucket
                .Where(r => r.Entry.DesiredVisibility == DesiredVisibility.Hidden
                         && _settings.WindowsHideMode == WindowsHideMode.HiddenAndSystem)
                .Select(r => r.Path)
                .ToList();

            var toShow = retryBucket
                .Where(r => r.Entry.DesiredVisibility == DesiredVisibility.Shown)
                .Select(r => r.Path)
                .ToList();

            var outcome = await Services.WindowsElevatedApplicator.ApplyAsync(toHide, toHideWithSystem, toShow);
            elevationExitCode = outcome.ExitCode;

            foreach (var row in retryBucket)
            {
                // Re-inspect only to refresh what the row shows; the success/error verdict
                // comes from the elevated child's own per-path report (see DecideElevatedRow).
                var recheck = await Task.Run(() => _visibilityService.Inspect(row.Path));
                bool? childOk = outcome.Results.TryGetValue(row.Path, out var ok) ? ok : null;

                var (display, wasApplied) = DecideElevatedRow(row.Entry.DesiredVisibility, childOk, recheck);
                row.ApplyScanResult(recheck with { ActualState = display }, row.PathFamily);

                if (wasApplied) applied++;
                else errors++;
            }
        }

        // elevationExitCode is a coarse diagnostic kept in the structured log; the user-facing
        // tally below is built per-path, so the raw child exit code is not surfaced to the UI.
        Log.Info("apply: done", new { applied, missing, errors, elevationExitCode });

        var parts = new List<string>();
        if (applied > 0) parts.Add($"{applied} applied");
        if (missing > 0) parts.Add($"{missing} missing");
        if (errors > 0) parts.Add($"{errors} errors");

        return parts.Count > 0 ? string.Join(", ", parts) : "nothing to do";
    }

    /// <summary>
    /// Decides one elevated-retry row's outcome from the elevated child's reported result
    /// (<paramref name="childOk"/>) and the parent's post-apply re-inspection
    /// (<paramref name="recheck"/>). Pure, so the verdict logic is testable without a real
    /// elevation.
    /// </summary>
    /// <remarks>
    /// <para><b>Verdict.</b> When the child reported a result, trust it: it is the only actor
    /// that actually attempted the change with the rights to do so. The unelevated parent may
    /// still read <see cref="ActualState.AccessDenied"/> on a path the child changed
    /// successfully (the very permission wall that forced elevation), so deriving success from
    /// re-inspection alone would falsely report an error. When the child reported nothing
    /// (<paramref name="childOk"/> is null — UAC cancelled, or the results file was unreadable)
    /// fall back to comparing the re-inspection against the desired state, which correctly
    /// yields "not applied" for the cancel case (nothing changed).</para>
    /// <para><b>Displayed state.</b> Prefer what the re-inspection could actually read. When it
    /// could not (AccessDenied/Error) but the child confirmed success, show the state the child
    /// achieved rather than the parent's blind spot.</para>
    /// </remarks>
    internal static (ActualState Display, bool Applied) DecideElevatedRow(
        DesiredVisibility desired, bool? childOk, PathInspection recheck)
    {
        var desiredState = desired == DesiredVisibility.Hidden ? ActualState.Hidden : ActualState.Visible;

        var applied = childOk ?? recheck.ActualState == desiredState;

        var readable = recheck.ActualState is ActualState.Hidden or ActualState.Visible or ActualState.Missing;
        var display = readable ? recheck.ActualState
                    : childOk == true ? desiredState
                    : recheck.ActualState;

        return (display, applied);
    }

    private CancellationTokenSource? _notificationCts;

    private void ShowNotification(string message)
    {
        // One line per user-visible outcome — the record of what the status bar showed.
        Log.Info("notification", new { message });
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
