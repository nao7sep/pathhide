using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;

namespace PathHide.ViewModels;

public partial class MainWindowViewModel : ObservableObject
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

    // Test seam (PathHide.Tests via InternalsVisibleTo): await the in-flight background scan
    // deterministically instead of polling IsScanning on a wall-clock budget. Returns whatever
    // scan is current, or Task.CompletedTask when none is running.
    internal Task ScanTask => _scanTask;
    private bool _initialized;
    private bool _persistedStateLoaded;

    /// <summary>
    /// Set by the view to show a destructive-action confirmation dialog. Returns true if the
    /// user confirms. Left null in headless contexts (tests), where the destructive action
    /// proceeds unprompted.
    /// </summary>
    public Func<ConfirmRequest, Task<bool>>? ConfirmDestructiveAsync { get; set; }

    /// <summary>
    /// Shows an informational notice (title, body). Supplied by the window, the
    /// same way <see cref="ConfirmDestructiveAsync"/> is.
    /// </summary>
    public Func<string, string, Task>? ShowNoticeAsync { get; set; }

    public ObservableCollection<PathRowViewModel> Rows { get; } = [];

    /// <summary>The path grid is mandatory, so it remains present and explains its empty body.</summary>
    public bool IsPathListEmpty => Rows.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _scanTotal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _scanProgress;

    [ObservableProperty]
    private bool _isScanning;

    public ObservableCollection<OperationalResultViewModel> OperationalResults { get; } = [];

    public bool HasOperationalResults => OperationalResults.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPathAddResult))]
    private PathAddResultViewModel? _pathAddResult;

    private readonly List<string> _pathAddIssuePaths = [];
    private bool _pathAddHasOpaqueIssues;

    public bool HasPathAddResult => PathAddResult is not null;

    // Settings are always available now: the UI font is a cross-platform setting, so the dialog

    /// <summary>Whether the Windows-only hide-mode setting applies; the dialog shows it only then.</summary>
    public bool HasWindowsHideMode { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Current Windows hide mode as a bool, used to seed the settings dialog. Read-only:
    /// the complete dialog draft is changed and persisted through <see cref="TryApplySettings"/>,
    /// never through a bound setter, so there is no save side effect on assignment.
    /// </summary>
    public bool IsHiddenAndSystem => _settings.WindowsHideMode == WindowsHideMode.HiddenAndSystem;

    /// <summary>The configured UI (chrome) font family, used to seed the settings dialog.</summary>
    public string UiFontFamily => _settings.UiFontFamily;

    public string ProgressText => ScanTotal > 0
        ? $"Scanning {ScanProgress} / {ScanTotal}"
        : string.Empty;

    public string StatusBarText => BuildSummary();

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
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsPathListEmpty));
        ApplyUiFont();
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

        LoadPersistedState();
        StartBackgroundScan();
    }

    /// <summary>Loads the path registry once, before either startup reporting or scanning.</summary>
    public void LoadPersistedState()
    {
        if (_persistedStateLoaded)
            return;
        _persistedStateLoaded = true;

        var loaded = _pathListStore.Load();
        if (loaded.WasUnreadable)
        {
            // The path list is the user's work product: a curated registry they
            // built, re-derivable from nothing else on disk. Opening with an
            // empty list would look exactly like losing it, and the first add
            // would then write a fresh file containing only that entry — the
            // user working on top of an apparent loss. The storage-path
            // conventions require a halt here; only re-derivable stores may
            // quarantine and continue.
            throw new PathListUnreadableException();
        }

        _entries = loaded.Value;
        SyncRowsWithEntries();
    }

    // --- The mutation protocol ---

    // Every mutating command runs under this gate. The generated AsyncRelayCommands each refuse a
    // second run of THEMSELVES, but nothing stopped Remove from landing in the middle of Hide's
    // apply — and each of them pauses the background scan, changes the list, and starts a scan
    // again afterwards. Serializing them is what makes "the scan I started is the scan that is
    // running" true by construction, rather than something the scan re-checks wherever it touches
    // shared state. Never disposed, deliberately: it lives as long as the window's view model, and
    // its wait handle is never materialized (nothing here reads AvailableWaitHandle), so there is
    // no unmanaged resource to release.
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    /// <summary>Runs <paramref name="body"/> exclusive of every other mutating command.</summary>
    private async Task UnderMutationGateAsync(Func<Task> body)
    {
        await _mutationGate.WaitAsync();
        try
        {
            await body();
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// The mutation protocol in one place: exclusive of every other mutating command, with the
    /// background scan paused for the body's duration and resumed after if it was running. Each
    /// command used to open and close with these two lines itself, which is a protocol that can
    /// be forgotten one command at a time.
    /// </summary>
    private Task MutateAsync(Func<Task> body) => UnderMutationGateAsync(async () =>
    {
        var scanWasActive = await PauseScanningAsync();
        try
        {
            await body();
        }
        finally
        {
            if (scanWasActive)
                StartBackgroundScan();
        }
    });

    // --- Add / Remove ---

    // Pickers and drag-drop share AddPathsCoreAsync; MutateAsync serializes two rapid drops or a
    // drop landing during a picker add.
    [RelayCommand]
    private Task AddPathsAsync(IEnumerable<string> paths) => AddPathsCoreAsync(paths, unavailable: 0);

    public Task AddDroppedPathsAsync(IEnumerable<string> paths, int unavailable) =>
        AddPathsCoreAsync(paths, Math.Max(0, unavailable));

    private Task AddPathsCoreAsync(IEnumerable<string> paths, int unavailable) => MutateAsync(async () =>
    {
        var added = 0;
        var duplicates = 0;
        var duplicatePaths = new List<string>();
        var invalid = unavailable;
        var addedPaths = new List<string>();
        var updated = new List<PathEntry>(_entries);

        foreach (var raw in paths)
        {
            if (!PathNormalizer.TryNormalize(raw, out var normalized, out _))
            {
                Log.Warn("add: rejected non-absolute path", new { path = raw });
                invalid++;
                continue;
            }

            if (updated.Any(e => PathNormalizer.AreEqual(e.Path, normalized)))
            {
                duplicates++;
                duplicatePaths.Add(normalized);
                continue;
            }

            updated.Add(new PathEntry
            {
                Path = normalized,
                DesiredVisibility = DesiredVisibility.Hidden,
            });
            addedPaths.Add(normalized);
            added++;
        }

        Log.Info("add paths", new { added, duplicates, invalid });

        if (added == 0)
        {
            ShowPathAddResult(added, duplicatePaths, invalid, ApplyOutcome.Empty);
            return;
        }

        if (!TrySaveEntries(updated, out var saveFailure))
        {
            SetPathAddResult(
                $"Could not add the selected paths because the path list could not be saved: {saveFailure}",
                PathAddResultSeverity.Error,
                issuePaths: addedPaths);
            return;
        }

        var newRows = Rows
            .Where(r => addedPaths.Any(path => PathNormalizer.AreEqual(path, r.Path)))
            .ToList();
        var outcome = await ApplyDesiredStateAsync(newRows);
        if (duplicates > 0 || invalid > 0 || outcome.HasProblems)
        {
            ShowPathAddResult(added, duplicatePaths, invalid, outcome);
        }
        else
        {
            ClearPathAddResultIfResolvedBy(addedPaths);
        }
    });

    private void ShowPathAddResult(
        int added,
        IReadOnlyCollection<string> duplicatePaths,
        int invalid,
        ApplyOutcome outcome)
    {
        var duplicates = duplicatePaths.Count;
        var parts = new List<string>();
        if (added > 0)
            parts.Add($"Added {added} path{(added == 1 ? string.Empty : "s")} to the list");
        if (outcome.Applied > 0)
            parts.Add($"{outcome.Applied} hidden");
        if (duplicates > 0)
            parts.Add(duplicates == 1 ? "1 path is already in the list" : $"{duplicates} paths are already in the list");
        if (invalid > 0)
            parts.Add(invalid == 1 ? "1 path was unavailable or invalid" : $"{invalid} paths were unavailable or invalid");
        if (outcome.Unchanged > 0)
            parts.Add(outcome.Unchanged == 1 ? "1 path did not become hidden" : $"{outcome.Unchanged} paths did not become hidden");
        if (outcome.Missing > 0)
            parts.Add(outcome.Missing == 1 ? "1 added path is missing" : $"{outcome.Missing} added paths are missing");
        if (outcome.Errors > 0)
            parts.Add(outcome.Errors == 1 ? "1 path could not be hidden" : $"{outcome.Errors} paths could not be hidden");

        if (parts.Count == 0)
            return;

        var severity = outcome.Errors > 0
            ? PathAddResultSeverity.Error
            : invalid > 0 || outcome.Unchanged > 0 || outcome.Missing > 0
                ? PathAddResultSeverity.Warning
                : PathAddResultSeverity.Information;

        SetPathAddResult(
            string.Join("; ", parts) + ".",
            severity,
            issuePaths: duplicatePaths.Concat(outcome.ProblemPaths),
            hasOpaqueIssues: invalid > 0);
    }

    private void SetPathAddResult(
        string message,
        PathAddResultSeverity severity,
        IEnumerable<string>? issuePaths = null,
        bool hasOpaqueIssues = false)
    {
        _pathAddIssuePaths.Clear();
        if (issuePaths is not null)
            _pathAddIssuePaths.AddRange(issuePaths.Distinct(StringComparer.Ordinal));
        _pathAddHasOpaqueIssues = hasOpaqueIssues;
        PathAddResult = new PathAddResultViewModel(message, severity);
        Log.Info("path add result", new { message, severity });
    }

    private void ClearPathAddResultIfResolvedBy(IEnumerable<string> resolvedPaths)
    {
        if (_pathAddHasOpaqueIssues || _pathAddIssuePaths.Count == 0)
            return;

        var resolved = resolvedPaths.ToList();
        if (_pathAddIssuePaths.All(issue => resolved.Any(path => PathNormalizer.AreEqual(issue, path))))
            DismissPathAddResult();
    }

    [RelayCommand]
    private void DismissPathAddResult()
    {
        _pathAddIssuePaths.Clear();
        _pathAddHasOpaqueIssues = false;
        PathAddResult = null;
    }

    [RelayCommand]
    private Task RemoveSelectedAsync() => MutateAsync(async () =>
    {
        var selected = Rows.Where(r => r.IsSelected).ToList();
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

        var removing = new HashSet<PathEntry>(selected.Select(row => row.Entry));
        var updated = _entries.Where(entry => !removing.Contains(entry)).ToList();

        Log.Info("remove paths", new { removed = selected.Count });

        if (!TrySaveEntries(updated, out var saveFailure))
        {
            ShowOperationalResult(OperationalResultOwner.PathStore, $"Failed to save: {saveFailure}", error: true);
            return;
        }

        ResolveOperationalResult(OperationalResultOwner.PathStore);
    });

    // --- Hide / Show ---

    /// <summary>
    /// The one shape every visibility command has: build the targets' new desired
    /// value, persist, apply, report — under the mutation gate, with the scan paused.
    /// </summary>
    /// <remarks>
    /// The four commands were four copies of this body differing only in which
    /// rows they took and which value they wrote, so any change to the mutation
    /// protocol had to land in all four and would be forgotten in one.
    /// <para><paramref name="selectTargets"/> is a callback rather than a list
    /// because the selection must be read AFTER the scan pause completes — the
    /// pause awaits, and the rows can change across it.</para>
    /// </remarks>
    private Task SetVisibilityAsync(
        Func<List<PathRowViewModel>> selectTargets,
        DesiredVisibility desired) => MutateAsync(async () =>
    {
        var targets = selectTargets();

        // An empty target set has nothing to persist. Like a full successful
        // visibility change, it is quiet and leaves the standing summary intact.
        if (targets.Count > 0)
        {
            var flipping = new HashSet<PathEntry>(targets.Select(row => row.Entry));
            var updated = _entries
                .Select(entry => flipping.Contains(entry)
                    ? new PathEntry { Path = entry.Path, DesiredVisibility = desired }
                    : entry)
                .ToList();

            // The rows take the new value from the save, not before it: TrySaveEntries
            // re-syncs them, so ApplyDesiredStateAsync below reads the committed state.
            if (!TrySaveEntries(updated, out var saveFailure))
            {
                ShowOperationalResult(OperationalResultOwner.PathStore, $"Failed to save: {saveFailure}", error: true);
                return;
            }
            ResolveOperationalResult(OperationalResultOwner.PathStore);
        }

        var outcome = await ApplyDesiredStateAsync(targets);
        ShowApplyOutcome(outcome);
        if (!outcome.HasProblems)
            ClearPathAddResultIfResolvedBy(targets.Select(row => row.Path));
    });

    private List<PathRowViewModel> SelectedRows() => Rows.Where(r => r.IsSelected).ToList();

    private List<PathRowViewModel> AllRows() => Rows.ToList();

    [RelayCommand]
    private Task HideSelectedAsync() => SetVisibilityAsync(SelectedRows, DesiredVisibility.Hidden);

    [RelayCommand]
    private Task ShowSelectedAsync() => SetVisibilityAsync(SelectedRows, DesiredVisibility.Shown);

    [RelayCommand]
    private Task HideAllAsync() => SetVisibilityAsync(AllRows, DesiredVisibility.Hidden);

    [RelayCommand]
    private Task ShowAllAsync() => SetVisibilityAsync(AllRows, DesiredVisibility.Shown);

    [RelayCommand]
    private Task ReapplyAllAsync() => MutateAsync(async () =>
    {
        var targets = Rows.ToList();
        var outcome = await ApplyDesiredStateAsync(targets);
        ShowApplyOutcome(outcome);
        if (!outcome.HasProblems)
            ClearPathAddResultIfResolvedBy(targets.Select(row => row.Path));
    });

    [RelayCommand]
    // Reload takes the gate like every other mutating command, but not MutateAsync's
    // pause-then-resume: it always ends by starting a fresh scan of the reloaded list,
    // rather than putting back the one it interrupted.
    private Task ReloadAsync() => UnderMutationGateAsync(async () =>
    {
        await PauseScanningAsync();

        // Reload reconciles the path list and re-scans. It deliberately does NOT reload
        // settings: the settings dialog saves before publishing its complete draft, so the
        // in-memory value never diverges from disk. Copying
        // a freshly loaded settings object back into the shared instance field-by-field would
        // be both brittle (it silently couples to AppSettings having one field) and pointless.
        Log.Info("reload");
        var reloaded = _pathListStore.Load();
        if (reloaded.WasUnreadable)
        {
            // Mid-session there is nothing to halt: the app is already running
            // and the rows on screen are the last good state. Keep them rather
            // than replacing them with an empty list, and say what happened.
            Log.Warn("reload: path list unreadable; keeping the loaded entries");
            await ReportQuarantinesAsync();
            StartBackgroundScan();
            return;
        }

        _entries = reloaded.Value;
        SyncRowsWithEntries();

        // A load can find the file unreadable and set it aside, and this one
        // happens long after startup — where the startup drain has already run
        // and will never run again. Without reporting here, pressing Reload on
        // a hand-edited-into-invalid paths.json emptied every row with no
        // notice, no explanation, and no pointer to the quarantined file.
        await ReportQuarantinesAsync();

        _scanTask = RunScanAsync();
        await _scanTask;
    });

    /// <summary>
    /// Tells the user about any store a load just set aside, if there is a
    /// window to tell them through. Startup has its own drain, because at that
    /// point no window exists yet to own the dialog.
    /// </summary>
    private async Task ReportQuarantinesAsync()
    {
        if (ShowNoticeAsync is null)
            return;

        var quarantined = QuarantineJournal.Drain();
        if (quarantined.Count == 0)
            return;

        var (title, body) = QuarantineJournal.Describe(quarantined);
        await ShowNoticeAsync(title, body);
    }

    /// <summary>Saves the complete Settings draft atomically and publishes it only after disk agrees.</summary>
    public string? TryApplySettings(string family, bool hiddenAndSystem)
    {
        family = UiFontFamilyValue.Normalize(family);
        var newMode = hiddenAndSystem ? WindowsHideMode.HiddenAndSystem : WindowsHideMode.HiddenOnly;
        if (_settings.UiFontFamily == family && _settings.WindowsHideMode == newMode)
            return null;

        var candidate = new AppSettings
        {
            UiFontFamily = family,
            WindowsHideMode = newMode,
        };
        try
        {
            _settingsStore.Save(candidate);
        }
        catch (Exception ex)
        {
            Log.Error("settings: save failed", ex);
            return ex.Message;
        }

        var fontChanged = _settings.UiFontFamily != family;
        var modeChanged = _settings.WindowsHideMode != newMode;
        _settings.UiFontFamily = family;
        _settings.WindowsHideMode = newMode;
        if (fontChanged)
        {
            ApplyUiFont();
            OnPropertyChanged(nameof(UiFontFamily));
        }
        if (modeChanged)
            OnPropertyChanged(nameof(IsHiddenAndSystem));
        Log.Info("settings: changed", new { family, mode = newMode });
        return null;
    }

    /// <summary>
    /// Applies the configured UI font app-wide by overriding the <c>AppFontFamily</c> resource the
    /// Window style binds via DynamicResource, so it takes effect live across every window.
    /// </summary>
    private void ApplyUiFont()
    {
        if (Application.Current is { } app)
        {
            app.Resources["AppFontFamily"] = UiFont.Resolve(_settings.UiFontFamily);
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
    }

    // --- Internals ---

    /// <summary>
    /// Persists <paramref name="updated"/> and, only if the save lands, makes it the live entry
    /// list and re-syncs the rows. Returns false when the save failed, having changed nothing.
    /// </summary>
    /// <remarks>
    /// Commit after save, never mutate-then-roll-back. The previous shape deep-cloned the list,
    /// applied the change to live state, and restored the clone when the save threw — so the
    /// correctness of every mutating command rested on each one remembering to snapshot at the
    /// right moment. Building the new list as a value makes a failed save a no-op by
    /// construction: nothing in memory moves until disk agrees.
    /// </remarks>
    private bool TrySaveEntries(List<PathEntry> updated, out string? failure)
    {
        failure = null;
        try
        {
            // Sort a snapshot so paths.json is diff-stable without imposing that order on the
            // live list. UI ordering is a separate concern handled by the DataGrid's own sort.
            _pathListStore.Save(updated.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (Exception ex)
        {
            Log.Error("paths: save failed", ex);
            failure = ex.Message;
            return false;
        }

        _entries = updated;
        SyncRowsWithEntries();
        return true;
    }

    private void StartBackgroundScan()
    {
        if (Rows.Count == 0)
            return;

        _scanTask = RunScanAsync();
    }

    /// <summary>
    /// Stops the background scan and waits for it to unwind, so the caller has the list to itself.
    /// Returns whether there was a scan to stop.
    /// </summary>
    /// <remarks>
    /// <c>_scanCts</c> is never a disposed source: the scan nulls the field and disposes the source
    /// in the same <c>finally</c>, with no await between them, so no other UI-thread work can run
    /// in the gap. This used to catch <see cref="ObjectDisposedException"/> around the cancel and
    /// return false — which, had it ever fired, would have told the caller there was no scan and
    /// left the paused one unresumed.
    /// </remarks>
    private async Task<bool> PauseScanningAsync()
    {
        var scanCts = _scanCts;
        if (scanCts is null)
            return false;

        scanCts.Cancel();

        if (!_scanTask.IsCompleted)
            await _scanTask;

        return true;
    }

    /// <summary>
    /// Brings <see cref="Rows"/> into agreement with <c>_entries</c>: existing rows are rebound to
    /// their entry (keeping the scanned state and the selection they carry), rows whose entry is
    /// gone are dropped, and new entries get a new row — all in the entries' own order.
    /// </summary>
    private void SyncRowsWithEntries()
    {
        var remainingRows = Rows.ToList();

        var desiredRows = new List<PathRowViewModel>(_entries.Count);

        foreach (var entry in _entries)
        {
            var existingIndex = remainingRows.FindIndex(row => PathNormalizer.AreEqual(row.Path, entry.Path));
            PathRowViewModel row;
            if (existingIndex < 0)
            {
                row = new PathRowViewModel(entry);
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
    }

    /// <summary>
    /// Scans the current entries in the background, updating each row as its result arrives.
    /// </summary>
    /// <remarks>
    /// At most one scan is ever live, and that is structural rather than checked: every start is
    /// either <see cref="Initialize"/> — once, before any command can run — or a mutating command
    /// holding <c>_mutationGate</c>, and every one of those cancels the running scan and AWAITS it
    /// before starting another. This method used to open by cancelling and disposing a "previous"
    /// source that cannot exist, and to gate each of its shared-state writes on
    /// <c>ReferenceEquals(_scanCts, scanCts)</c> — four re-checks of one invariant, in the type
    /// least able to enforce it.
    /// </remarks>
    private async Task RunScanAsync()
    {
        var scanCts = new CancellationTokenSource();
        _scanCts = scanCts;
        var token = scanCts.Token;
        var entries = _entries.ToList();
        var completed = false;

        IsScanning = true;
        ScanTotal = entries.Count;
        ScanProgress = 0;

        try
        {
            await foreach (var result in _scanner.ScanAsync(entries, token))
            {
                var row = Rows.FirstOrDefault(r => r.Entry == result.Entry);
                row?.ApplyScanResult(result.Inspection, result.Family);
                // The results ARE the progress: counting them here keeps the number the status
                // bar shows in lockstep with the rows, instead of arriving on its own channel.
                ScanProgress++;
            }
            completed = true;
        }
        catch (OperationCanceledException)
        {
            Log.Info("scan: cancelled");
        }
        catch (Exception ex)
        {
            Log.Error("scan: failed", ex);
            ShowOperationalResult(OperationalResultOwner.Scan, $"Scan failed: {ex.Message}", error: true);
        }
        finally
        {
            if (completed)
                ResolveOperationalResult(OperationalResultOwner.Scan);
            _scanCts = null;
            IsScanning = false;
            OnPropertyChanged(nameof(StatusBarText));
            scanCts.Dispose();
        }
    }

    private async Task<ApplyOutcome> ApplyDesiredStateAsync(List<PathRowViewModel> targets)
    {
        Log.Info("apply: start", new { count = targets.Count });

        var applied = 0;
        var unchanged = 0;
        var missing = 0;
        var errors = 0;
        var problemPaths = new List<string>();
        var retryBucket = new List<PathRowViewModel>();

        foreach (var row in targets)
        {
            try
            {
                var inspection = await Task.Run(() => _visibilityService.Inspect(row.Path));

                if (inspection.ActualState == ActualState.Missing)
                {
                    missing++;
                    problemPaths.Add(row.Path);
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
                    problemPaths.Add(row.Path);
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

                // Count what actually moved, not what was attempted. A write can
                // run without changing the state the user asked for — on macOS a
                // dot-prefixed path stays hidden by its name whatever the flags
                // say, and this app cannot rename files. Reporting it as applied
                // told the user a path had been revealed while it was still
                // invisible in Finder. The row already shows the mismatch; this
                // keeps the summary honest about it.
                var desiredState = row.Entry.DesiredVisibility == DesiredVisibility.Hidden
                    ? ActualState.Hidden
                    : ActualState.Visible;
                if (updated.ActualState == desiredState)
                {
                    applied++;
                }
                else
                {
                    unchanged++;
                    problemPaths.Add(row.Path);
                    Log.Info("apply: state unchanged", new
                    {
                        path = row.Path,
                        desired = desiredState,
                        actual = updated.ActualState,
                    });
                }
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
                problemPaths.Add(row.Path);
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
            var buckets = Services.ElevatedApplyCommand.Partition(
                retryBucket.Select(r => (r.Path, r.Entry.DesiredVisibility)),
                _settings.WindowsHideMode);

            var outcome = await Services.WindowsElevatedApplicator.ApplyAsync(
                buckets.ToHide, buckets.ToHideWithSystem, buckets.ToShow);
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
                else
                {
                    errors++;
                    problemPaths.Add(row.Path);
                }
            }
        }

        // elevationExitCode is a coarse diagnostic kept in the structured log; the user-facing
        // tally below is built per-path, so the raw child exit code is not surfaced to the UI.
        Log.Info("apply: done", new { applied, unchanged, missing, errors, elevationExitCode });
        OnPropertyChanged(nameof(StatusBarText));

        return new ApplyOutcome(applied, unchanged, missing, errors, problemPaths);
    }

    private readonly record struct ApplyOutcome(
        int Applied,
        int Unchanged,
        int Missing,
        int Errors,
        IReadOnlyList<string> ProblemPaths)
    {
        public static ApplyOutcome Empty { get; } = new(0, 0, 0, 0, []);

        public bool HasProblems => Unchanged > 0 || Missing > 0 || Errors > 0;

        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (Applied > 0) parts.Add($"{Applied} applied");
                if (Unchanged > 0) parts.Add($"{Unchanged} unchanged");
                if (Missing > 0) parts.Add($"{Missing} missing");
                if (Errors > 0) parts.Add($"{Errors} errors");
                return parts.Count > 0 ? string.Join(", ", parts) : "Nothing to do";
            }
        }
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

    private void ShowApplyOutcome(ApplyOutcome outcome)
    {
        if (outcome.Errors > 0)
        {
            ShowOperationalResult(OperationalResultOwner.Visibility, outcome.Summary, error: true);
            return;
        }

        if (outcome.HasProblems)
        {
            ShowOperationalResult(OperationalResultOwner.Visibility, outcome.Summary, error: false);
            return;
        }

        ResolveOperationalResult(OperationalResultOwner.Visibility);
    }

    private void ShowOperationalResult(OperationalResultOwner owner, string message, bool error)
    {
        Log.Info("operational result", new { owner, message, error });
        ResolveOperationalResult(owner);
        OperationalResults.Add(new OperationalResultViewModel(owner, message, error));
        OnPropertyChanged(nameof(HasOperationalResults));
    }

    [RelayCommand]
    private void DismissOperationalResult(OperationalResultViewModel result)
    {
        if (OperationalResults.Remove(result))
            OnPropertyChanged(nameof(HasOperationalResults));
    }

    private void ResolveOperationalResult(OperationalResultOwner owner)
    {
        var result = OperationalResults.FirstOrDefault(item => item.Owner == owner);
        if (result is not null && OperationalResults.Remove(result))
            OnPropertyChanged(nameof(HasOperationalResults));
    }

}
