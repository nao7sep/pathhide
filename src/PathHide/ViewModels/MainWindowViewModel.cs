using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PathHide.Backup;
using PathHide.I18n;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;

namespace PathHide.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IJsonStore<List<PathEntry>> _pathListStore;
    private readonly ISettingsStore _settingsStore;
    private readonly IJsonStore<AppState> _stateStore;
    private readonly BoundedVisibility _visibility;
    private readonly PathScanner _scanner;
    private readonly BoundedStoreWork _pathWork = new();
    private readonly BoundedStoreWork _settingsWork = new();
    private readonly BoundedStoreWork _stateWork = new();
    internal TimeSpan StorageWaitBound { get; init; } = BoundedStoreWork.DefaultBound;

    // The same AppSettings instance the Windows visibility service closes over (wired in
    // App's composition root). Mutate its fields in place; never reassign the reference,
    // or the service would read stale state.
    private readonly AppSettings _settings;

    // The window state as last saved; replaced only once a save of its successor lands.
    private AppState _state;

    private List<PathEntry> _entries = [];

    // Whether paths.json holds _entries. False after rejected Reload or uncertain save settlement;
    // a rejected Reload separately blocks saves until the repaired list is successfully read.
    private bool _entriesOnDisk = true;
    // A rejected Reload must not let old rows overwrite an unreadable/ambiguous hand edit.
    // The supported repair workflow is edit, then successfully Reload before another action.
    private Exception? _pathListReadFailure;
    internal TimeSpan IdentityWaitBound { get; init; } = TimeSpan.FromSeconds(2);
    // False after a Settings save failed or timed out: it may still have landed, so the settings held in
    // memory no longer prove what config.json holds, and the next Settings save writes whatever it holds.
    private bool _settingsOnDisk = true;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _applyCts;

    private Task _scanTask = Task.CompletedTask;

    // Test seam (PathHide.Tests via InternalsVisibleTo): await the in-flight background scan
    // deterministically instead of polling IsScanning on a wall-clock budget. Returns whatever
    // scan is current, or Task.CompletedTask when none is running.
    internal Task ScanTask => _scanTask;
    private bool _initialized;

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
    public Func<Message, Message, Task>? ShowNoticeAsync { get; set; }

    /// <summary>
    /// Asks whether a quit the user started may stop the Hide/Show still running; true stops it and
    /// quits. Null where nothing can ask, as in tests that do not set it.
    /// </summary>
    public Func<Task<bool>>? ConfirmStopAtQuitAsync { get; set; }

    /// <summary>
    /// Set by the view to ask, as a quit the user started stops, whether to save again or quit with
    /// the lines' changes unsaved. Left null in headless contexts (tests), where the quit goes on.
    /// </summary>
    public Func<IReadOnlyList<Message>, Task<UnsavedQuitChoice>>? AskUnsavedAtQuitAsync { get; set; }

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
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isScanning;

    /// <summary>Whether a visibility change is being applied to disk.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isApplying;

    /// <summary>Whether there is a scan or an apply for <see cref="CancelCommand"/> to stop.</summary>
    public bool IsBusy => IsScanning || IsApplying;

    /// <summary>
    /// Whether a list-changing command (Add, Hide, Show, Reapply, Reload or Remove) is running. While
    /// one runs the others are unavailable rather than queueing out of sight behind it; Cancel stays.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddPathsCommand), nameof(RemoveSelectedCommand), nameof(HideSelectedCommand),
        nameof(ShowSelectedCommand), nameof(HideAllCommand), nameof(ShowAllCommand), nameof(ReapplyAllCommand),
        nameof(ReloadCommand))]
    private bool _isCommandRunning;

    /// <summary>Whether a list-changing command can start now.</summary>
    public bool CanStartCommand => !IsCommandRunning;

    public ObservableCollection<OperationalResultViewModel> OperationalResults { get; } = [];

    public bool HasOperationalResults => OperationalResults.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPathAddResult))]
    private PathAddResultViewModel? _pathAddResult;

    private readonly List<string> _pathAddIssuePaths = [];
    // Whether the issue paths are entries in the list (a problem applying them, or a duplicate of one),
    // which an entry leaving the list resolves, rather than paths that were never added.
    private bool _pathAddIssuesAreTracked;
    private bool _pathAddHasOpaqueIssues;
    private bool _pathAddResultIsPickerFailure;

    public bool HasPathAddResult => PathAddResult is not null;

    /// <summary>Whether the Windows-only hide-mode setting applies; the dialog shows it only then.</summary>
    public bool HasWindowsHideMode { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Current Windows hide mode as a bool, used to seed the settings dialog. Read-only:
    /// the complete dialog draft is changed and persisted through <see cref="TryApplySettingsAsync"/>,
    /// never through a bound setter, so there is no save side effect on assignment.
    /// </summary>
    public bool IsHiddenAndSystem => _settings.WindowsHideMode == WindowsHideMode.HiddenAndSystem;

    /// <summary>The configured UI (chrome) font family, used to seed the settings dialog.</summary>
    public string UiFontFamily => _settings.UiFontFamily;

    /// <summary>The saved theme, used to seed the settings dialog.</summary>
    public ThemePreference Theme => _settings.Theme;

    /// <summary>The saved display zone: <see cref="DisplayZone.System"/> or an IANA zone.</summary>
    public string DisplayTimeZone => _settings.DisplayTimeZone;

    /// <summary>The saved language preference — a tag or System — used to seed the settings dialog.</summary>
    public string Language => Languages.NormalizePreference(_settings.Language);

    /// <summary>
    /// The computer's own languages, in order, as <c>LanguageBootstrap</c> read them at launch. A
    /// language saved in Settings resolves System against this same list, so System cannot mean one
    /// language at launch and another after a Save.
    /// </summary>
    internal IReadOnlyList<string> ComputerLanguages { get; init; } = [];

    /// <summary>
    /// The elevated retry for access-denied paths, or null where the platform has no elevation step
    /// (everywhere but Windows), in which case access-denied is a terminal error.
    /// </summary>
    internal IElevatedApplicator? ElevatedApplicator { get; init; }

    /// <summary>
    /// How long a quit waits, in all, for a running command, the user's saves, pending backup history and
    /// an elevated child. After it come <see cref="PlacementSaveBound"/> for each window, then, once the
    /// window has gone, the session log's drain (<see cref="SessionLogger.DrainBound"/>, or
    /// <see cref="SessionLogger.SessionEndDrainBound"/> when the OS is ending the session) and the
    /// single-instance lease's release (at most 2 s, normally immediate). At logout the whole stays near
    /// 3 s plus placement plus 1 s (unsaved-edits-conventions, Quitting).
    /// </summary>
    internal TimeSpan ShutdownBound { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a window's placement save may hold its close.</summary>
    internal TimeSpan PlacementSaveBound { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The clock the activation rescan's quiet period is measured on; tests replace it.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// How long after an activation rescan starts a further activation starts none, so a burst of
    /// activations (window switching, a dialog closing as the app comes forward) costs one rescan.
    /// </summary>
    internal static readonly TimeSpan ActivationRescanQuietPeriod = TimeSpan.FromSeconds(1);

    private long? _lastActivationRescan;
    public int? WindowPositionX => _state.WindowPositionX;
    public int? WindowPositionY => _state.WindowPositionY;
    public double? WindowWidth => _state.WindowWidth;
    public double? WindowHeight => _state.WindowHeight;
    public bool WindowMaximized => _state.WindowMaximized;

    public string ProgressText => ScanTotal > 0
        ? Localizer.T("status.scanning", ("done", ScanProgress), ("total", ScanTotal))
        : string.Empty;

    public string StatusBarText => Localizer.Of(Summary());

    // The status bar counts the rows' states, so it is read again whenever a row joins, leaves or changes
    // state, wherever that happens.
    private void OnRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (PathRowViewModel row in e.OldItems ?? Array.Empty<PathRowViewModel>())
            row.PropertyChanged -= OnRowChanged;
        foreach (PathRowViewModel row in e.NewItems ?? Array.Empty<PathRowViewModel>())
            row.PropertyChanged += OnRowChanged;
        OnPropertyChanged(nameof(IsPathListEmpty));
        OnPropertyChanged(nameof(StatusBarText));
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PathRowViewModel.ActualState))
            OnPropertyChanged(nameof(StatusBarText));
    }

    /// <summary>
    /// The status bar's line: the entry count and each non-zero state, each counted in its own
    /// sentence so it takes its own plural form, joined as the language joins them.
    /// </summary>
    internal Message Summary()
    {
        // An empty list explains itself inside the list; the bar only counts.
        var hidden = Rows.Count(r => r.ActualState == ActualState.Hidden);
        var visible = Rows.Count(r => r.ActualState == ActualState.Visible);
        var missing = Rows.Count(r => r.ActualState == ActualState.Missing);
        var pending = Rows.Count(r => r.ActualState == ActualState.Unknown);
        var problems = Rows.Count(r => r.ActualState is ActualState.AccessDenied or ActualState.Error or ActualState.Unresponsive);

        var parts = new List<Message> { Message.Of("status.entries", ("count", Rows.Count)) };
        if (hidden > 0) parts.Add(Message.Of("status.hidden", ("count", hidden)));
        if (visible > 0) parts.Add(Message.Of("status.visible", ("count", visible)));
        if (missing > 0) parts.Add(Message.Of("status.missing", ("count", missing)));
        if (pending > 0) parts.Add(Message.Of("status.pending", ("count", pending)));
        if (problems > 0) parts.Add(Message.Of("status.problems", ("count", problems)));
        return Message.Join("status.join", parts);
    }

    /// <summary>
    /// Brings every word this view model has on screen into the current language. The window calls
    /// it when the language changes while it is open: an empty name tells every binding on this view
    /// model to re-read, and the rows and result strips, which hold their own words, re-announce
    /// theirs.
    /// </summary>
    internal void Retranslate()
    {
        OnPropertyChanged(string.Empty);
        foreach (var row in Rows)
            row.Retranslate();
        foreach (var result in OperationalResults)
            result.Retranslate();
        PathAddResult?.Retranslate();
    }

    /// <summary>
    /// All dependencies are supplied by the composition root (see <c>App</c>),
    /// including the already-loaded <paramref name="settings"/> and <paramref name="state"/>. The Windows
    /// visibility service behind <paramref name="visibility"/> closes over that same
    /// instance to read the current hide mode, so the view model mutates it in place
    /// rather than replacing it.
    /// </summary>
    /// <remarks>
    /// Construction is side-effect-free: no disk I/O and no scan happen here, so the
    /// type is safe to instantiate outside a running app. Call <see cref="Initialize"/>
    /// once the view is ready to start scanning; persisted values must already be prepared.
    /// </remarks>
    public MainWindowViewModel(
        BoundedVisibility visibility,
        IJsonStore<List<PathEntry>> pathListStore,
        ISettingsStore settingsStore,
        AppSettings settings,
        IJsonStore<AppState> stateStore,
        AppState state,
        List<PathEntry> entries)
    {
        _visibility = visibility;
        _pathListStore = pathListStore;
        _settingsStore = settingsStore;
        _settings = settings;
        _stateStore = stateStore;
        _state = state;
        _entries = entries;
        _scanner = new PathScanner(visibility);
        DisplayZone.Use(settings.DisplayTimeZone);
        Rows.CollectionChanged += OnRowsChanged;
        ApplyUiFont();
        SyncRowsWithEntries();
    }

    /// <summary>
    /// Starts the initial background scan over the prepared entries. The view
    /// calls this once it is loaded. Idempotent — only the first call has any effect,
    /// so a repeated Loaded event cannot trigger a second scan.
    /// </summary>
    public void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;

        StartBackgroundScan();
    }

    internal Task ValidateLoadedPathsAsync() => ValidateLoadedPathsAsync(_entries, CancellationToken.None);

    private async Task ValidateLoadedPathsAsync(List<PathEntry> entries, CancellationToken token)
    {
        // Native identities are optional evidence, independent of the required local-store worker.
        // One overall budget stops a large/offline list from delaying startup or Reload indefinitely.
        using var deadline = new CancellationTokenSource(IdentityWaitBound, Clock);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var keys = entries.Select(entry =>
        {
            PathNormalizer.TryNormalize(entry.Path, out var normalized, out var family);
            return (Path: normalized!, Family: family);
        }).ToList();

        async Task<(string Path, string Identity)> ResolveAsync(string path)
        {
            try { return (path, await _visibility.ResolvePathAsync(path, waiting.Token) ?? path); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
            {
                // The native read remains owned; its late result can never revise this admission.
                return (path, path);
            }
        }

        var spellings = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var (path, identity) in BoundedVisibility.InCompletionOrderAsync(
            keys.Select(key => key.Path).Distinct(StringComparer.Ordinal), ResolveAsync))
            spellings[path] = identity;

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, family) in keys)
        {
            if (!identities.Add(PathNormalizer.IdentityKey(spellings[path], family)))
                throw new UnreadableStoreException("paths", Path.Combine(StorageRoot.Directory, PathListStore.FileName),
                    new JsonException("The path list holds aliases of the same local entry."));
        }
        token.ThrowIfCancellationRequested();
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
        _commandsInFlight++;
        IsCommandRunning = true;
        await _mutationGate.WaitAsync();
        try
        {
            // A command that reaches the gate once a quit has begun does not start; the quit takes it.
            if (_quitting)
                return;

            await body();
        }
        finally
        {
            _mutationGate.Release();
            IsCommandRunning = --_commandsInFlight > 0;
        }
    }

    // Commands that have reached the gate, waiting or running; touched on the UI thread only.
    private int _commandsInFlight;

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
    [RelayCommand(CanExecute = nameof(CanStartCommand))]
    private Task AddPathsAsync(IEnumerable<string> paths) => AddPathsCoreAsync(paths, unavailable: 0);

    public Task AddDroppedPathsAsync(IEnumerable<string> paths, int unavailable) =>
        AddPathsCoreAsync(paths, Math.Max(0, unavailable));

    public void ReportPathPickerFailure(Exception error)
    {
        SetPathAddResult(
            FailurePresentation.PathPicker(error),
            PathAddResultSeverity.Error,
            hasOpaqueIssues: true);
        _pathAddResultIsPickerFailure = true;
    }

    public void ResolvePathPickerFailure()
    {
        if (_pathAddResultIsPickerFailure)
            DismissPathAddResult();
    }

    public void ReportWindowActionFailure(Exception error) =>
        ShowOperationalResult(
            OperationalResultOwner.Window,
            FailurePresentation.WindowAction(error),
            error: true);

    /// <remarks>
    /// Adding is the act of hiding, so the whole add — deciding each path's identity, saving, hiding —
    /// runs as one cancellable operation, with Cancel shown throughout.
    /// </remarks>
    private Task AddPathsCoreAsync(IEnumerable<string> paths, int unavailable) => MutateAsync(() => RunCancellableAsync(async token =>
    {
        var added = 0;
        var duplicates = 0;
        var duplicatePaths = new List<string>();
        var invalid = unavailable;
        var addedPaths = new List<string>();
        var updated = new List<PathEntry>(_entries);

        var accepted = new List<string>();
        foreach (var raw in paths)
        {
            if (!PathNormalizer.TryNormalize(raw, out var normalized, out _))
            {
                Log.Warn("add: rejected non-absolute path", new { path = raw });
                invalid++;
                continue;
            }
            accepted.Add(normalized);
        }

        var identities = await ResolveIdentitiesAsync(_entries.Select(entry => entry.Path).Concat(accepted).ToList(), token);
        // Cancel while identities are still being decided adds nothing: no entry is saved or hidden.
        if (identities is null)
            return;
        var existing = identities.Take(_entries.Count).ToList();
        foreach (var candidate in identities.Skip(_entries.Count))
        {
            if (existing.Any(path => PathNormalizer.AreEqual(path, candidate)))
            {
                duplicates++;
                duplicatePaths.Add(candidate);
                continue;
            }

            // Keep the physical parent so Show still reaches the entry hidden by Add if a
            // parent alias is retargeted. The final link remains the entry, never its target.
            updated.Add(new PathEntry
            {
                Path = candidate,
                DesiredVisibility = DesiredVisibility.Hidden,
            });
            addedPaths.Add(candidate);
            existing.Add(candidate);
            added++;
        }

        Log.Info("add paths", new { added, duplicates, invalid, paths = addedPaths, duplicatePaths });

        if (added == 0)
        {
            ShowPathAddResult(added, duplicatePaths, invalid, ApplyOutcome.Empty);
            return;
        }

        var saveFailure = await TrySaveEntriesAsync(updated);
        if (saveFailure is not null)
        {
            SetPathAddResult(
                saveFailure,
                PathAddResultSeverity.Error,
                issuePaths: addedPaths);
            return;
        }

        // Rows carry their entry's stored string, so an exact match finds the new ones.
        var addedSet = new HashSet<string>(addedPaths, StringComparer.Ordinal);
        var newRows = Rows.Where(r => addedSet.Contains(r.Path)).ToList();
        var outcome = await ApplyAsync(newRows, token);
        if (duplicates > 0 || invalid > 0 || outcome.HasProblems)
        {
            ShowPathAddResult(added, duplicatePaths, invalid, outcome);
        }
        else
        {
            ResolvePathAddIssues(issue => addedPaths.Any(path => PathNormalizer.AreEqual(issue, path)));
        }
    }));

    /// <summary>
    /// Resolve incoming and existing references at Add's bounded native boundary. This recognizes
    /// old alias spellings without rewriting stored entries, and distinguishes case-only Mac files.
    /// A path whose identity cannot be read in time (an unavailable volume) keeps its normalized
    /// spelling; later comparisons remain pure. Null when the user cancelled before every identity was
    /// decided, so Add stops before saving anything.
    /// </summary>
    private async Task<List<string>?> ResolveIdentitiesAsync(List<string> paths, CancellationToken token)
    {
        var keys = paths.Select(path =>
        {
            PathNormalizer.TryNormalize(path, out var normalized, out _);
            return normalized ?? path;
        }).ToList();
        // A few at a time, so an entry on a volume that stopped answering holds back only itself.
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await foreach (var (key, identity) in BoundedVisibility.InCompletionOrderAsync(
                keys.Distinct(StringComparer.Ordinal),
                async key => (key, await _visibility.ResolvePathAsync(key, token) ?? key)))
                resolved[key] = identity;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Log.Info("add: cancelled while resolving paths; nothing was added");
            return null;
        }
        return [.. keys.Select(key => resolved[key])];
    }

    private void ShowPathAddResult(
        int added,
        IReadOnlyCollection<string> duplicatePaths,
        int invalid,
        ApplyOutcome outcome)
    {
        var duplicates = duplicatePaths.Count;
        var parts = new List<Message>();
        if (added > 0)
            parts.Add(Message.Of("result.added", ("count", added)));
        if (outcome.Applied > 0)
            parts.Add(Message.Of("result.hidden", ("count", outcome.Applied)));
        if (duplicates > 0)
            parts.Add(Message.Of("result.duplicates", ("count", duplicates)));
        if (invalid > 0)
            parts.Add(Message.Of("result.invalid", ("count", invalid)));
        if (outcome.Unchanged > 0)
            parts.Add(Message.Of("result.unchanged", ("count", outcome.Unchanged)));
        if (outcome.Missing > 0)
            parts.Add(Message.Of("result.missing", ("count", outcome.Missing)));
        if (outcome.Errors > 0)
            parts.Add(Message.Of("result.errors", ("count", outcome.Errors)));
        if (outcome.Unresponsive > 0)
            parts.Add(Message.Of("result.unresponsive", ("count", outcome.Unresponsive)));
        if (outcome.Cancelled > 0)
            parts.Add(Message.Of("result.cancelled", ("count", outcome.Cancelled)));
        if (outcome.ElevationBusy > 0)
            parts.Add(Message.Of("result.elevationBusy", ("count", outcome.ElevationBusy)));
        if (outcome.ElevationDeclined > 0)
            parts.Add(Message.Of("result.elevationDeclined", ("count", outcome.ElevationDeclined)));

        if (parts.Count == 0)
            return;

        var severity = outcome.HasFailures
            ? PathAddResultSeverity.Error
            : invalid > 0 || outcome.HasProblems
                ? PathAddResultSeverity.Warning
                : PathAddResultSeverity.Information;

        SetPathAddResult(
            Message.Of("result.summary", "parts", Message.Join("result.join", parts)),
            severity,
            issuePaths: duplicatePaths.Concat(outcome.ProblemPaths),
            hasOpaqueIssues: invalid > 0,
            issuesAreTracked: true);
    }

    private void SetPathAddResult(
        Message message,
        PathAddResultSeverity severity,
        IEnumerable<string>? issuePaths = null,
        bool hasOpaqueIssues = false,
        bool issuesAreTracked = false)
    {
        _pathAddResultIsPickerFailure = false;
        _pathAddIssuePaths.Clear();
        if (issuePaths is not null)
            _pathAddIssuePaths.AddRange(issuePaths.Distinct(StringComparer.Ordinal));
        _pathAddHasOpaqueIssues = hasOpaqueIssues;
        _pathAddIssuesAreTracked = issuesAreTracked;
        PathAddResult = new PathAddResultViewModel(message, severity);
        Log.Info("path add result", new { message, severity });
    }

    /// <summary>
    /// Drops the add result's issue paths that <paramref name="resolved"/> settles, one by one as the
    /// visibility results do, and the result itself once none is left. An issue that names no path
    /// (an invalid drop) keeps it until it is dismissed.
    /// </summary>
    private void ResolvePathAddIssues(Func<string, bool> resolved)
    {
        if (_pathAddHasOpaqueIssues || _pathAddIssuePaths.Count == 0)
            return;

        _pathAddIssuePaths.RemoveAll(issue => resolved(issue));
        if (_pathAddIssuePaths.Count == 0)
            DismissPathAddResult();
    }

    /// <summary>A path an apply reached without a problem settles the add result's issue about it.</summary>
    private void ResolvePathAddIssues(IEnumerable<PathRowViewModel> targets, ApplyOutcome outcome)
    {
        var settled = targets.Select(row => row.Path).Where(path => !outcome.Problems.ContainsKey(path)).ToList();
        ResolvePathAddIssues(issue => settled.Any(path => PathNormalizer.AreEqual(issue, path)));
    }

    [RelayCommand]
    private void DismissPathAddResult()
    {
        _pathAddResultIsPickerFailure = false;
        _pathAddIssuePaths.Clear();
        _pathAddHasOpaqueIssues = false;
        _pathAddIssuesAreTracked = false;
        PathAddResult = null;
    }

    /// <summary>
    /// Drops the selected entries from the list and from <c>paths.json</c>. Each item's visibility is
    /// left exactly as it is.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartCommand))]
    private Task RemoveSelectedAsync()
    {
        var paths = SelectedPaths();
        return MutateAsync(async () =>
        {
            var selected = TrackedTargets(paths);
            if (selected.Count == 0)
                return;

            if (ConfirmDestructiveAsync is not null)
            {
                var confirmed = await ConfirmDestructiveAsync(new ConfirmRequest(
                    Message.Of("remove.title"),
                    Message.Of("remove.message", ("count", selected.Count)),
                    "common.remove"));

                if (!confirmed)
                    return;
            }

            var removing = new HashSet<PathEntry>(selected.Select(row => row.Entry));
            var updated = _entries.Where(entry => !removing.Contains(entry)).ToList();

            Log.Info("remove paths", new { removed = selected.Count, paths = selected.Select(row => row.Path) });

            var saveFailure = await TrySaveEntriesAsync(updated);
            if (saveFailure is not null)
            {
                ShowOperationalResult(OperationalResultOwner.PathStore, saveFailure, error: true);
                return;
            }

            ResolveOperationalResult(OperationalResultOwner.PathStore);
        });
    }

    // --- Hide / Show ---

    /// <summary>
    /// The one shape every visibility command has: build the targets' new desired
    /// value, persist, apply, report — under the mutation gate, with the scan paused.
    /// </summary>
    /// <remarks>
    /// The four commands were four copies of this body differing only in which
    /// rows they took and which value they wrote, so any change to the mutation
    /// protocol had to land in all four and would be forgotten in one.
    /// <para>Capture submitted path identities before waiting. Resolve their current rows under the
    /// gate so selection changes cannot redirect the action and removed entries cannot return.</para>
    /// <para>Desired visibility in <c>paths.json</c> is authoritative; the rows show the actual attribute
    /// state. There is no transaction or rollback between the two: the save lands first, so an apply that
    /// fails, is cancelled or is cut short by a quit leaves desired state ahead of the attributes. The next
    /// scan shows the difference, and applying again repairs it.</para>
    /// </remarks>
    private Task SetVisibilityAsync(
        IReadOnlySet<string> paths,
        DesiredVisibility desired) => MutateAsync(async () =>
    {
        var targets = TrackedTargets(paths);
        Log.Info("set visibility", new { desired, count = targets.Count });

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

            // The rows take the new value from the save, not before it: TrySaveEntriesAsync
            // re-syncs them, so ApplyDesiredStateAsync below reads the committed state.
            var saveFailure = await TrySaveEntriesAsync(updated);
            if (saveFailure is not null)
            {
                ShowOperationalResult(OperationalResultOwner.PathStore, saveFailure, error: true);
                return;
            }
            ResolveOperationalResult(OperationalResultOwner.PathStore);
        }

        var outcome = await ApplyDesiredStateAsync(targets);
        ShowApplyOutcome(outcome, targets.Select(row => row.Path));
        ResolvePathAddIssues(targets, outcome);
    });

    private HashSet<string> SelectedPaths() => Rows.Where(row => row.IsSelected)
        .Select(row => row.Path).ToHashSet(StringComparer.Ordinal);

    private HashSet<string> AllPaths() => Rows.Select(row => row.Path).ToHashSet(StringComparer.Ordinal);

    private List<PathRowViewModel> TrackedTargets(IReadOnlySet<string> paths) =>
        Rows.Where(row => paths.Contains(row.Path)).ToList();

    [RelayCommand(CanExecute = nameof(CanStartCommand))]
    private Task HideSelectedAsync() => SetVisibilityAsync(SelectedPaths(), DesiredVisibility.Hidden);

    [RelayCommand(CanExecute = nameof(CanStartCommand))]
    private Task ShowSelectedAsync() => SetVisibilityAsync(SelectedPaths(), DesiredVisibility.Shown);

    [RelayCommand(CanExecute = nameof(CanStartCommand))]
    private Task HideAllAsync() => SetVisibilityAsync(AllPaths(), DesiredVisibility.Hidden);

    [RelayCommand(CanExecute = nameof(CanStartCommand))]
    private Task ShowAllAsync() => SetVisibilityAsync(AllPaths(), DesiredVisibility.Shown);

    [RelayCommand(CanExecute = nameof(CanStartCommand))]
    private Task ReapplyAllAsync()
    {
        var paths = AllPaths();
        return MutateAsync(async () =>
        {
            var targets = TrackedTargets(paths);
            Log.Info("reapply all", new { count = targets.Count });
            var outcome = await ApplyDesiredStateAsync(targets);
            ShowApplyOutcome(outcome, targets.Select(row => row.Path));
            ResolvePathAddIssues(targets, outcome);
        });
    }

    [RelayCommand(CanExecute = nameof(CanStartCommand))]
    // Reload takes the gate like every other mutating command, but not MutateAsync's
    // pause-then-resume: it always ends by starting a fresh background scan of the reloaded
    // list, rather than putting back the one it interrupted. That scan runs after the gate is
    // released, like every other, so the next command pauses it instead of queueing behind it.
    private Task ReloadAsync() => UnderMutationGateAsync(() => RunCancellableAsync(async token =>
    {
        await PauseScanningAsync();

        // Reload reconciles the path list and re-scans. It deliberately does NOT reload
        // settings: only the Settings dialog publishes configuration changes to the live instance;
        // reloading the path list does not change the app's configuration.
        Log.Info("reload");
        // Off the UI thread: the data folder may sit on a slow or redirected profile share.
        LoadedStore<List<PathEntry>> reloaded;
        try
        {
            reloaded = await _pathWork.RunAsync(_pathListStore.Load, StorageWaitBound, Clock, token);
            await ValidateLoadedPathsAsync(reloaded.Value, token);
        }
        catch (Exception ex) when (ex is NewerFormatException or UnreadableStoreException)
        {
            // Mid-session there is nothing to halt: the rows on screen are the last good state. Keep
            // them and refuse saves until a successful Reload, including native alias ambiguity.
            _pathListReadFailure = ex;
            _entriesOnDisk = false;
            if (ShowNoticeAsync is not null)
                await ShowNoticeAsync(Message.Of("failure.pathListReloadTitle"), ex is NewerFormatException newer
                    ? FailurePresentation.NewerStore(newer)
                    : FailurePresentation.PathListUnreadable((UnreadableStoreException)ex));
            StartBackgroundScan();
            return;
        }

        catch (OperationCanceledException)
        {
            StartBackgroundScan();
            return;
        }
        catch (Exception ex)
        {
            Log.Error("reload: read failed", ex);
            ShowOperationalResult(OperationalResultOwner.PathStore, Message.Of("failure.pathListReload"), error: true);
            StartBackgroundScan();
            return;
        }

        _entries = reloaded.Value;
        _pathListReadFailure = null;
        _entriesOnDisk = true;
        lock (_userSavesGate)
            _unsavedAtQuit.Remove(UserStore.PathList);
        SyncRowsWithEntries();
        StartBackgroundScan();
    }));

    /// <summary>
    /// Rescans the path list because the window came back to the front, so a path unhidden, moved
    /// or deleted outside the app since the last scan shows its state without a Reload. Rows are
    /// updated in place, so the selection and every result on screen stay. Starts nothing before the
    /// first scan, while a scan or any command runs, once closing has begun, or within
    /// <see cref="ActivationRescanQuietPeriod"/> of the last activation rescan. Returns whether a
    /// rescan started.
    /// </summary>
    public bool RescanOnActivation()
    {
        if (!_initialized || IsScanning)
            return false;

        var now = Clock.GetTimestamp();
        if (_lastActivationRescan is { } last && Clock.GetElapsedTime(last, now) < ActivationRescanQuietPeriod)
            return false;

        // Taken without waiting: a command holding the gate (a quit included, which keeps it until the
        // app exits or the quit is called off) means this is no moment to scan, and the command starts
        // its own scan when it is done.
        if (!_mutationGate.Wait(0))
            return false;
        try
        {
            if (Rows.Count == 0)
                return false;

            _lastActivationRescan = now;
            Log.Info("scan: rescanning on activation", new { entries = Rows.Count });
            StartBackgroundScan();
            return true;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Saves the Settings draft atomically, off the UI thread, and publishes the draft only after
    /// disk agrees. Returns what to tell the reader when the save failed, or null when it landed.
    /// The draft is compared with the settings held in memory, or always written after a save whose
    /// outcome is unknown; <c>config.json</c> is not read.
    /// </summary>
    public async Task<Message?> TryApplySettingsAsync(
        string language, string family, bool hiddenAndSystem, ThemePreference theme, string? timeZone = null)
    {
        language = Languages.NormalizePreference(language);
        timeZone = timeZone is null ? _settings.DisplayTimeZone : DisplayZone.NormalizePreference(timeZone);
        family = UiFontFamilyValue.Normalize(family);
        var newMode = hiddenAndSystem ? WindowsHideMode.HiddenAndSystem : WindowsHideMode.HiddenOnly;

        var previous = CopySettings();
        var candidate = CopySettings();
        candidate.Language = language;
        candidate.UiFontFamily = family;
        candidate.WindowsHideMode = newMode;
        candidate.Theme = theme;
        candidate.DisplayTimeZone = timeZone;
        bool changed;
        try
        {
            changed = await RunUserSaveAsync(
                UserStore.Settings,
                () => _settingsStore.SaveChanges(_settingsOnDisk ? previous : null, candidate),
                FailurePresentation.SettingsSave,
                () => TryApplySettingsAsync(language, family, hiddenAndSystem, theme, timeZone));
        }
        catch (Exception ex)
        {
            Log.Error("settings: save failed", ex);
            _settingsOnDisk = false;
            return FailurePresentation.SettingsSave(ex);
        }
        _settingsOnDisk = true;

        if (!changed && previous.Language == candidate.Language
            && previous.UiFontFamily == candidate.UiFontFamily
            && previous.WindowsHideMode == candidate.WindowsHideMode
            && previous.Theme == candidate.Theme
            && previous.DisplayTimeZone == candidate.DisplayTimeZone)
            return null;

        _settings.Language = candidate.Language;
        _settings.UiFontFamily = candidate.UiFontFamily;
        _settings.WindowsHideMode = candidate.WindowsHideMode;
        _settings.Theme = candidate.Theme;
        _settings.DisplayTimeZone = candidate.DisplayTimeZone;

        if (previous.UiFontFamily != family)
        {
            ApplyUiFont();
            OnPropertyChanged(nameof(UiFontFamily));
        }
        if (previous.WindowsHideMode != newMode)
            OnPropertyChanged(nameof(IsHiddenAndSystem));
        if (previous.Theme != theme)
            OnPropertyChanged(nameof(Theme));
        if (previous.DisplayTimeZone != timeZone)
            OnPropertyChanged(nameof(DisplayTimeZone));
        OnPropertyChanged(nameof(Language));
        Log.Info("settings: changed", new { language, family, mode = newMode, theme, timeZone });

        // Last, once disk agrees: a language or zone change redraws everything already on screen.
        DisplayZone.Use(timeZone);
        Localizer.Use(language, ComputerLanguages);
        return null;
    }

    /// <summary>
    /// Saves the window placement to the state store, never the settings. It holds the close on
    /// purpose: the write must finish before the process exits, or it would be cut off and the
    /// placement lost when the app quits. It holds it for at most <see cref="PlacementSaveBound"/>.
    /// An unchanged placement writes nothing; a failed or unfinished save throws and leaves the live
    /// placement untouched.
    /// </summary>
    public void SaveWindowPlacement(int x, int y, double width, double height, bool maximized) =>
        SaveStateForClose(state => state with
        {
            WindowPositionX = x,
            WindowPositionY = y,
            WindowWidth = width,
            WindowHeight = height,
            WindowMaximized = maximized,
        });

    public int? RecordsWindowPositionX => _state.RecordsWindowPositionX;
    public int? RecordsWindowPositionY => _state.RecordsWindowPositionY;
    public double? RecordsWindowWidth => _state.RecordsWindowWidth;
    public double? RecordsWindowHeight => _state.RecordsWindowHeight;
    public bool RecordsWindowMaximized => _state.RecordsWindowMaximized;

    /// <summary>The records window's list width as last dragged, or null before the first drag.</summary>
    public double? RecordsListWidth => _state.RecordsListWidth;

    /// <summary>
    /// Saves the records window's placement as it closes, synchronously for the reason
    /// <see cref="SaveWindowPlacement"/> gives.
    /// </summary>
    public void SaveRecordsWindowPlacement(int x, int y, double width, double height, bool maximized) =>
        SaveStateForClose(state => state with
        {
            RecordsWindowPositionX = x,
            RecordsWindowPositionY = y,
            RecordsWindowWidth = width,
            RecordsWindowHeight = height,
            RecordsWindowMaximized = maximized,
        });

    /// <summary>
    /// Saves the width the records window's list was dragged to, off the UI thread: a drag ends while
    /// the window is in use, not as it closes. A failed save faults the task and changes nothing.
    /// </summary>
    public Task SaveRecordsListWidthAsync(double width) =>
        SaveStateAsync(state => state with { RecordsListWidth = width }, StorageWaitBound);

    // Window-state operations share physical ordering, including after a close abandons its wait.
    private void SaveStateForClose(Func<AppState, AppState> change) =>
        SaveStateAsync(change, PlacementSaveBound).GetAwaiter().GetResult();

    private Task<bool> SaveStateAsync(Func<AppState, AppState> change, TimeSpan bound) =>
        _stateWork.RunAsync(() =>
        {
            var candidate = change(_state);
            if (candidate == _state)
                return false;
            _stateStore.Save(candidate);
            _state = candidate;
            return true;
        }, bound, Clock);

    private AppSettings CopySettings() => new()
    {
        Language = _settings.Language,
        UiFontFamily = _settings.UiFontFamily,
        Theme = _settings.Theme,
        WindowsHideMode = _settings.WindowsHideMode,
        DisplayTimeZone = _settings.DisplayTimeZone,
    };

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

    /// <summary>Stops the running scan, or the running apply, whichever there is.</summary>
    [RelayCommand]
    private void Cancel()
    {
        _scanCts?.Cancel();
        _applyCts?.Cancel();
    }

    // --- Quitting (unsaved-edits-conventions, Quitting) ---

    private Task<bool>? _shutdown;

    // From a quit's start until the app exits or the user calls the quit off: no command starts, an apply
    // that starts is cancelled at once, and a save of the user's that fails is kept for Retry.
    private volatile bool _quitting;

    // Whether the quit holds the mutation gate. It keeps it, so nothing starts behind it, until the app
    // exits or the quit is called off.
    private bool _quitHoldsGate;

    private bool _sessionEnding;

    // The user's own saves still running (the path list's and the settings'), which a quit waits for, and
    // each one that failed while a quit waited, kept with what to tell the user so Retry can save it again.
    private readonly object _userSavesGate = new();
    private readonly HashSet<Task> _userSaves = [];
    private readonly Dictionary<UserStore, UnsavedChange> _unsavedAtQuit = [];

    private enum UserStore { PathList, Settings }

    private sealed record UnsavedChange(Message Failure, Func<Task> SaveAgain);

    /// <summary>
    /// Whether closing has work to finish first: a scan or apply, a save of the user's, pending backup
    /// history, or an elevated child still running.
    /// </summary>
    public bool HasWorkToFinish => IsBusy || UserSavesRunning || HasUnsavedChanges
        || BackupStore.HasPendingWrites || ElevatedApplicator?.HasRunningChild == true;

    private bool HasUnsavedChanges
    {
        get { lock (_userSavesGate) return _unsavedAtQuit.Count > 0; }
    }

    private bool UserSavesRunning
    {
        get
        {
            lock (_userSavesGate)
                return _userSaves.Count > 0;
        }
    }

    /// <summary>
    /// The waits every quit runs, within <see cref="ShutdownBound"/> in all: cancels the scan and the
    /// apply, takes the mutation gate so no command starts after it, saves again what a save that failed
    /// during an earlier round of this quit left unsaved, waits for the user's saves still running, gives
    /// pending backup history the rest of the bound unless the OS session is ending, and gives a
    /// still-running elevated child the chance to exit so its temp files are removed now rather
    /// than by the next launch. It never asks anything. Returns whether everything of the user's is on
    /// disk. Idempotent while it runs: a second close joins the first.
    /// </summary>
    public Task<bool> ShutdownAsync() => _shutdown ??= ShutdownCoreAsync();

    /// <summary>
    /// The quit the user started: menu Quit, Cmd+Q, Dock Quit, or closing the main window. When a save of
    /// the user's failed or is still running at the bound, the quit stops and the user chooses Retry or
    /// Quit anyway; dismissing that question calls the quit off and keeps the app open. Returns true
    /// when the app may exit.
    /// </summary>
    public async Task<bool> QuitAsync()
    {
        // A running Hide/Show is the app's own work on the user's files, so a quit the user started asks
        // before stopping it (unsaved-edits-conventions, Window-owned and app-owned work). A scan only
        // refreshes what the window shows, so it ends with the window unasked. Once the quit's waits
        // have begun, the work is already being stopped and a joining close asks nothing.
        if (_shutdown is null && IsApplying && !_sessionEnding && ConfirmStopAtQuitAsync is { } confirmStop)
        {
            bool stop;
            try
            {
                stop = await confirmStop();
            }
            catch (Exception ex)
            {
                Log.Error("quit: stop question failed; keeping the app open", ex);
                CallOffQuit();
                return false;
            }
            if (_sessionEnding)
                return false;
            if (!stop)
            {
                CallOffQuit();
                return false;
            }
            Log.Info("quit: stopping the running visibility change");
        }

        while (!await ShutdownAsync())
        {
            // Once the session is ending, its own quit has taken over.
            if (_sessionEnding || AskUnsavedAtQuitAsync is null)
                return !_sessionEnding;

            UnsavedQuitChoice choice;
            try
            {
                choice = await AskUnsavedAtQuitAsync(UnsavedAtQuitLines());
            }
            catch (Exception ex)
            {
                Log.Error("quit: unsaved changes question failed; keeping the app open", ex);
                CallOffQuit();
                return false;
            }
            if (_sessionEnding)
                return false;

            switch (choice)
            {
                case UnsavedQuitChoice.Retry:
                    _shutdown = null;
                    continue;
                case UnsavedQuitChoice.QuitAnyway:
                    Log.Warn("quit: quitting with the user's changes unsaved", UnsavedAtQuitFields());
                    return true;
                default:
                    CallOffQuit();
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The operating system is logging out, restarting or shutting down: the same waits as any quit,
    /// within the same bound, and never a question. What did not land is logged and the app exits.
    /// </summary>
    public async Task EndSessionAsync()
    {
        _sessionEnding = true;
        if (!await ShutdownAsync())
            Log.Error("shutdown: the session ended with the user's changes unsaved", UnsavedAtQuitFields());
    }

    private async Task<bool> ShutdownCoreAsync()
    {
        var started = Clock.GetTimestamp();
        TimeSpan Remaining()
        {
            var left = ShutdownBound - Clock.GetElapsedTime(started);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        _quitting = true;
        KeyValuePair<UserStore, UnsavedChange>[] saveAgain;
        lock (_userSavesGate)
            saveAgain = [.. _unsavedAtQuit];

        Log.Info("shutdown: finishing work in flight",
            new { busy = IsBusy, saving = UserSavesRunning, saveAgain = saveAgain.Length });
        Cancel();

        if (!_quitHoldsGate)
        {
            _quitHoldsGate = await TakeGateAsync(Remaining());
            if (!_quitHoldsGate)
                Log.Warn("shutdown: a command did not finish within the bound");
        }
        _scanCts?.Cancel();

        foreach (var (store, change) in saveAgain)
        {
            // The path list saves only under the gate; while a command still holds it, the change stays
            // unsaved and the user is asked again.
            if (store == UserStore.PathList && !_quitHoldsGate)
                continue;

            try
            {
                await change.SaveAgain().WaitAsync(Remaining(), Clock);
            }
            catch (TimeoutException)
            {
                // Still running, and waited for below.
            }
        }

        await WaitForUserSavesAsync(Remaining());

        // Backup history is best effort: an ordinary quit gives pending history what remains of the bound,
        // and the end of the OS session does not wait for it (data-backup-conventions).
        if (!_sessionEnding)
            await BackupStore.FlushAsync(Remaining(), Clock);

        if (ElevatedApplicator is { } elevated)
            await elevated.ReleaseAsync(Remaining());

        lock (_userSavesGate)
            return _userSaves.Count == 0 && _unsavedAtQuit.Count == 0;
    }

    private async Task<bool> TakeGateAsync(TimeSpan bound)
    {
        if (bound <= TimeSpan.Zero)
            return _mutationGate.Wait(0);

        using var timeout = new CancellationTokenSource(bound, Clock);
        try
        {
            await _mutationGate.WaitAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task WaitForUserSavesAsync(TimeSpan bound)
    {
        Task[] running;
        lock (_userSavesGate)
            running = [.. _userSaves];
        if (running.Length == 0)
            return;

        try
        {
            await Task.WhenAll(running).WaitAsync(bound, Clock);
        }
        catch (TimeoutException)
        {
            Log.Warn("shutdown: a save did not finish within the bound", new { running = running.Length });
        }
        catch (Exception)
        {
            // Each failed save has logged itself and, as the quit waited, kept its change for Retry.
        }
    }

    /// <summary>What the quit's question tells the user: each failed save, any still running, and what quitting costs.</summary>
    internal IReadOnlyList<Message> UnsavedAtQuitLines()
    {
        var lines = new List<Message>();
        lock (_userSavesGate)
        {
            lines.AddRange(_unsavedAtQuit.OrderBy(pair => pair.Key).Select(pair => pair.Value.Failure));
            if (_userSaves.Count > 0)
                lines.Add(Message.Of("quit.stillSaving"));
        }

        lines.Add(Message.Of("quit.unsavedMessage"));
        return lines;
    }

    private object UnsavedAtQuitFields()
    {
        lock (_userSavesGate)
            return new { unsaved = _unsavedAtQuit.Keys.Select(store => store.ToString()).ToArray(), running = _userSaves.Count };
    }

    // The user dismissed the question: the app stays open as it was before the quit. A change whose save
    // failed is dropped, as any failed save is; the window already shows its failure.
    internal void CancelQuit() => CallOffQuit();

    private void CallOffQuit()
    {
        _quitting = false;
        _shutdown = null;
        lock (_userSavesGate)
            _unsavedAtQuit.Clear();
        if (_quitHoldsGate)
        {
            _quitHoldsGate = false;
            _mutationGate.Release();
        }

        Log.Info("quit: called off; the app stays open");
    }

    /// <summary>
    /// Runs one of the user's own saves off the UI thread, tracked while it runs so a quit waits for it.
    /// A save that fails while a quit waits keeps its change, with what to tell the user, for Retry; the
    /// record is made before the task ends, so a quit that has waited for the task sees it.
    /// </summary>
    private Task<T> RunUserSaveAsync<T>(
        UserStore store, Func<T> save, Func<Exception, Message> describe, Func<Task> saveAgain)
    {
        var wait = RunUserSaveCoreAsync(store, save, describe, saveAgain);
        TrackUserSave(wait);
        return wait;
    }

    private void TrackUserSave(Task task)
    {
        lock (_userSavesGate)
            _userSaves.Add(task);
        _ = task.ContinueWith(done =>
        {
            lock (_userSavesGate)
                _userSaves.Remove(done);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<T> RunUserSaveCoreAsync<T>(
        UserStore store, Func<T> save, Func<Exception, Message> describe, Func<Task> saveAgain)
    {
        var owner = store == UserStore.PathList ? _pathWork : _settingsWork;
        try
        {
            var result = await owner.RunAsync(save, StorageWaitBound, Clock, started: TrackUserSave);
            lock (_userSavesGate)
                _unsavedAtQuit.Remove(store);
            return result;
        }
        catch (Exception ex)
        {
            // A timeout leaves an unknown physical outcome, retained for Reload/Retry and quit.
            lock (_userSavesGate)
            {
                if (_quitting || ex is TimeoutException || _unsavedAtQuit.ContainsKey(store))
                    _unsavedAtQuit[store] = new UnsavedChange(describe(ex), saveAgain);
            }
            throw;
        }
    }

    // --- Internals ---

    /// <summary>
    /// Persists <paramref name="updated"/> off the UI thread and, only if the save lands, makes it the
    /// live entry list and re-syncs the rows. Returns what to tell the user when the save failed,
    /// having changed nothing, or null when it landed. A list whose content equals the saved one
    /// writes nothing and changes nothing (content-lifecycle conventions, Files).
    /// </summary>
    /// <remarks>
    /// Commit after save, never mutate-then-roll-back. The previous shape deep-cloned the list,
    /// applied the change to live state, and restored the clone when the save threw — so the
    /// correctness of every mutating command rested on each one remembering to snapshot at the
    /// right moment. Building the new list as a value makes a failed save a no-op by
    /// construction: nothing in memory moves until disk agrees.
    /// <para>The save is a write-then-rename, and the data folder may sit on a redirected profile
    /// share, so it never runs on the UI thread. Every caller holds the mutation gate, so saves stay ordered.</para>
    /// </remarks>
    private async Task<Message?> TrySaveEntriesAsync(List<PathEntry> updated)
    {
        if (_pathListReadFailure is { } failure)
            return FailurePresentation.PathListSave(failure);

        // Sort a snapshot so paths.json is diff-stable without imposing that order on the
        // live list. UI ordering is a separate concern handled by the DataGrid's own sort.
        var snapshot = SortedForDisk(updated);

        if (_entriesOnDisk && SameContent(snapshot, SortedForDisk(_entries)))
        {
            Log.Debug("paths: unchanged, not saved");
            lock (_userSavesGate)
                _unsavedAtQuit.Remove(UserStore.PathList);
            return null;
        }

        try
        {
            await RunUserSaveAsync(
                UserStore.PathList,
                () =>
                {
                    _pathListStore.Save(snapshot);
                    return true;
                },
                FailurePresentation.PathListSave,
                () => TrySaveEntriesAsync(updated));
        }
        catch (Exception ex)
        {
            if (ex is TimeoutException)
                _entriesOnDisk = false;
            Log.Error("paths: save failed", ex);
            return FailurePresentation.PathListSave(ex);
        }

        _entries = updated;
        _entriesOnDisk = true;
        SyncRowsWithEntries();
        return null;
    }

    private static List<PathEntry> SortedForDisk(IEnumerable<PathEntry> entries) =>
        entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Whether two lists in disk order hold the same paths with the same desired visibility.</summary>
    private static bool SameContent(List<PathEntry> left, List<PathEntry> right) =>
        left.Count == right.Count
        && left.Zip(right).All(pair =>
            string.Equals(pair.First.Path, pair.Second.Path, StringComparison.Ordinal)
            && pair.First.DesiredVisibility == pair.Second.DesiredVisibility);

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
        // Keyed by the stored path: a row carries its entry's exact string, and every entry was
        // given its identity when it was added, so matching is a lookup, not a comparison per pair.
        var remainingRows = Rows
            .GroupBy(row => row.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new Queue<PathRowViewModel>(group), StringComparer.Ordinal);

        var desiredRows = new List<PathRowViewModel>(_entries.Count);

        foreach (var entry in _entries)
        {
            PathRowViewModel row;
            if (remainingRows.TryGetValue(entry.Path, out var matches) && matches.TryDequeue(out var existing))
            {
                row = existing;
                row.SyncEntry(entry);
            }
            else
            {
                row = new PathRowViewModel(entry);
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

        var tracked = _entries.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        ResolveVisibilityResults(path => !tracked.Contains(path));
        // The same rule for the add result: an entry that left the list has no issue left to report.
        if (_pathAddIssuesAreTracked)
            ResolvePathAddIssues(issue => !_entries.Any(entry => PathNormalizer.AreEqual(entry.Path, issue)));
    }

    /// <summary>
    /// Scans the current entries in the background, updating each row as its result arrives.
    /// </summary>
    /// <remarks>
    /// At most one scan is ever live, and that is structural rather than checked: every start is
    /// either <see cref="Initialize"/> — once, before any command can run — or a mutating command
    /// holding <c>_mutationGate</c>, and every one of those cancels the running scan and AWAITS it
    /// before starting another; <see cref="RescanOnActivation"/> holds the gate too, and starts only
    /// when no scan is running. This method used to open by cancelling and disposing a "previous"
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
            ShowOperationalResult(OperationalResultOwner.Scan, FailurePresentation.Scan(ex), error: true);
        }
        finally
        {
            if (completed)
                ResolveOperationalResult(OperationalResultOwner.Scan);
            _scanCts = null;
            IsScanning = false;
            scanCts.Dispose();
        }
    }

    /// <summary>
    /// Applies each target's desired visibility, cancellable through <see cref="CancelCommand"/>.
    /// </summary>
    /// <remarks>
    /// Every file-system call goes through <see cref="BoundedVisibility"/>, so one stalled network
    /// share or half-ejected volume costs its path a bounded wait and an Unresponsive verdict, never
    /// the whole command: the caller holds the mutation gate throughout, and an unbounded stat here
    /// once kept every other command queued behind it until the app was force-quit.
    /// </remarks>
    private Task<ApplyOutcome> ApplyDesiredStateAsync(List<PathRowViewModel> targets) =>
        RunCancellableAsync(token => ApplyAsync(targets, token));

    /// <summary>
    /// Runs <paramref name="body"/> as the one operation <see cref="CancelCommand"/> stops, with
    /// <see cref="IsApplying"/> set for its duration.
    /// </summary>
    private async Task<T> RunCancellableAsync<T>(Func<CancellationToken, Task<T>> body)
    {
        using var applyCts = new CancellationTokenSource();
        _applyCts = applyCts;
        // An apply that follows a save the quit waited for stops at once.
        if (_quitting)
            applyCts.Cancel();
        IsApplying = true;
        try
        {
            return await body(applyCts.Token);
        }
        finally
        {
            // Nulled before the using disposes the source, with no await between, so Cancel never
            // reaches a disposed source.
            _applyCts = null;
            IsApplying = false;
        }
    }

    private Task RunCancellableAsync(Func<CancellationToken, Task> body) =>
        RunCancellableAsync(async token =>
        {
            await body(token);
            return true;
        });

    private async Task<ApplyOutcome> ApplyAsync(List<PathRowViewModel> targets, CancellationToken token)
    {
        // Which paths are to be hidden and which shown: the outcome names only the paths with problems,
        // so these are the record of what every other path was changed to.
        Log.Info("apply: start", new
        {
            count = targets.Count,
            hide = targets.Where(row => row.Entry.DesiredVisibility == DesiredVisibility.Hidden).Select(row => row.Path),
            show = targets.Where(row => row.Entry.DesiredVisibility == DesiredVisibility.Shown).Select(row => row.Path),
        });

        // Settings stay open to the user while a batch runs; every path in it is hidden the same way.
        var hideMode = _settings.WindowsHideMode;

        var applied = 0;
        var problems = new Dictionary<string, ApplyProblem>(StringComparer.Ordinal);
        var retryBucket = new List<PathRowViewModel>();

        void MarkUnresponsive(PathRowViewModel row)
        {
            problems[row.Path] = ApplyProblem.Unresponsive;
            row.ActualState = ActualState.Unresponsive;
        }

        // Paths are worked on a few at a time, so a stalled one costs its own bound rather than
        // delaying every path behind it; each result is taken here, one at a time, on this thread.
        var cancelled = 0;
        var attempts = BoundedVisibility.InCompletionOrderAsync(targets, row => AttemptAsync(row, hideMode, token));
        await foreach (var attempt in attempts)
        {
            var row = attempt.Row;
            if (attempt.Cancelled)
            {
                // A path whose write was under way may or may not have changed — the call was
                // abandoned, not undone — so its row no longer claims a state. The rest were never
                // written.
                if (attempt.Written)
                    row.ActualState = ActualState.Unknown;
                problems[row.Path] = ApplyProblem.Cancelled;
                cancelled++;
                continue;
            }

            switch (attempt.Failure)
            {
                case TimeoutException:
                    MarkUnresponsive(row);
                    continue;
                case UnauthorizedAccessException when ElevatedApplicator is not null:
                    // Access-denied is recoverable via a single elevated retry (below).
                    retryBucket.Add(row);
                    continue;
                case not null:
                    problems[row.Path] = ApplyProblem.Error;
                    row.ApplyScanResult(attempt.After!, row.PathFamily);
                    continue;
            }

            if (!attempt.Written)
            {
                var inspection = attempt.Before!;
                if (inspection.ActualState == ActualState.Unresponsive)
                    MarkUnresponsive(row);
                else if (inspection.ActualState == ActualState.Missing)
                {
                    problems[row.Path] = ApplyProblem.Missing;
                    row.ActualState = ActualState.Missing;
                }
                // Access-denied at inspect time is the same recoverable condition as a denied
                // Hide/Show write: a single elevated retry (drained below) may have the rights to read
                // and change it, so it joins that bucket rather than the write attempt, which would
                // only re-hit the same denial. Without an elevation step AccessDenied stays a terminal
                // error alongside Error, which no elevation can fix. A genuinely absent path is
                // Missing (handled above), never AccessDenied, so this never forces a futile
                // elevation prompt.
                else if (inspection.ActualState == ActualState.AccessDenied && ElevatedApplicator is not null)
                    retryBucket.Add(row);
                else
                {
                    problems[row.Path] = ApplyProblem.Error;
                    row.ActualState = inspection.ActualState;
                }
                continue;
            }

            var updated = attempt.After!;
            if (updated.ActualState == ActualState.Unresponsive)
            {
                MarkUnresponsive(row);
                continue;
            }
            row.ApplyScanResult(updated, row.PathFamily);

            // Count what actually moved, not what was attempted. A write can run without changing
            // the state the user asked for: on macOS a dot-prefixed path stays hidden by its name
            // whatever the flags say, and this app cannot rename files. The row already shows the
            // mismatch; this keeps the summary honest about it.
            var desiredState = row.Entry.DesiredVisibility == DesiredVisibility.Hidden
                ? ActualState.Hidden
                : ActualState.Visible;
            if (updated.ActualState == desiredState)
            {
                applied++;
            }
            else
            {
                problems[row.Path] = ApplyProblem.Unchanged;
                Log.Info("apply: state unchanged", new
                {
                    path = row.Path,
                    desired = desiredState,
                    actual = updated.ActualState,
                });
            }
        }
        if (cancelled > 0)
            Log.Info("apply: cancelled", new { skipped = cancelled });
        // Results arrive as paths answer; the elevated request keeps the list's order.
        retryBucket = [.. targets.Intersect(retryBucket)];

        int? elevationExitCode = null;

        // A cancelled apply does not go on to raise an elevation prompt: the retry rows are
        // counted as cancelled, untouched.
        if (retryBucket.Count > 0 && token.IsCancellationRequested)
        {
            foreach (var row in retryBucket)
                problems[row.Path] = ApplyProblem.Cancelled;
        }
        else if (retryBucket.Count > 0 && ElevatedApplicator is { } elevated)
        {
            var buckets = Services.ElevatedApplyCommand.Partition(
                retryBucket.Select(r => (r.Path, r.Entry.DesiredVisibility)),
                hideMode);

            // The outcome is final once this returns: a child still running after a cancel or a
            // timeout never reports again, so its late writes cannot reach these rows.
            var outcome = await elevated.ApplyAsync(buckets, token);
            elevationExitCode = outcome.ExitCode;

            foreach (var row in retryBucket)
            {
                var reported = outcome.Results.TryGetValue(row.Path, out var ok);

                // The wait ended, not the child: a path it had not reported on may still change,
                // so its row no longer claims a state.
                if (!reported && outcome.Status is ElevatedApplyStatus.Cancelled or ElevatedApplyStatus.TimedOut)
                {
                    row.ActualState = ActualState.Unknown;
                    problems[row.Path] = outcome.Status == ElevatedApplyStatus.Cancelled
                        ? ApplyProblem.Cancelled : ApplyProblem.Unresponsive;
                    continue;
                }

                // The user cancelled: a row the child reported takes its own verdict as it stands, and
                // the next scan refreshes what it shows. Re-inspecting here could not be cancelled, and
                // would keep the apply running row by row after Cancel.
                if (reported && outcome.Status == ElevatedApplyStatus.Cancelled)
                {
                    var desiredState = row.Entry.DesiredVisibility == DesiredVisibility.Hidden
                        ? ActualState.Hidden
                        : ActualState.Visible;
                    row.ActualState = ok ? desiredState : ActualState.Unknown;
                    if (ok) applied++;
                    else
                    {
                        problems[row.Path] = ApplyProblem.Error;
                    }
                    continue;
                }

                // Re-inspect only to refresh what the row shows; the success/error verdict
                // comes from the elevated child's own per-path report (see DecideElevatedRow).
                var recheck = await _visibility.InspectAsync(row.Path, CancellationToken.None);

                // Refused while an earlier child still runs: nothing was attempted, so the row shows
                // what is on disk and the summary says why.
                if (outcome.Status == ElevatedApplyStatus.Busy)
                {
                    row.ApplyScanResult(recheck, row.PathFamily);
                    problems[row.Path] = ApplyProblem.ElevationBusy;
                    continue;
                }

                // The user declined administrator approval: nothing was attempted, which is their
                // choice rather than a failure, so the row shows what is on disk and the summary says so.
                if (outcome.Status == ElevatedApplyStatus.Declined)
                {
                    row.ApplyScanResult(recheck, row.PathFamily);
                    problems[row.Path] = ApplyProblem.ElevationDeclined;
                    continue;
                }

                var (display, wasApplied) = DecideElevatedRow(
                    row.Entry.DesiredVisibility, reported ? ok : null, recheck);
                row.ApplyScanResult(recheck with { ActualState = display }, row.PathFamily);

                if (wasApplied) applied++;
                else
                {
                    problems[row.Path] = ApplyProblem.Error;
                }
            }
        }

        // elevationExitCode is a coarse diagnostic kept in the structured log; the user-facing
        // tally below is built per-path, so the raw child exit code is not surfaced to the UI.
        var result = new ApplyOutcome(applied, problems);
        Log.Info("apply: done", new { applied, unchanged = result.Unchanged, missing = result.Missing,
            errors = result.Errors, unresponsive = result.Unresponsive, cancelled = result.Cancelled,
            elevationBusy = result.ElevationBusy, elevationDeclined = result.ElevationDeclined, elevationExitCode });

        return result;
    }

    /// <summary>
    /// One target's file-system work in an apply: what it was before, whether it was written, and what
    /// it is after, or how that ended. It touches no row, so several can run at once.
    /// </summary>
    private async Task<ApplyAttempt> AttemptAsync(PathRowViewModel row, WindowsHideMode hideMode, CancellationToken token)
    {
        var (path, desired) = (row.Path, row.Entry.DesiredVisibility);
        var written = false;
        try
        {
            var before = await _visibility.InspectAsync(path, token);
            if (before.ActualState is ActualState.Unresponsive or ActualState.Missing
                or ActualState.AccessDenied or ActualState.Error)
                return new(row) { Before = before };

            written = true;
            await _visibility.WriteAsync(path, desired, hideMode, token);
            return new(row) { Before = before, Written = true, After = await _visibility.InspectAsync(path, token) };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new(row) { Written = written, Cancelled = true };
        }
        catch (Exception ex) when (ex is TimeoutException
            || (ex is UnauthorizedAccessException && ElevatedApplicator is not null))
        {
            return new(row) { Written = written, Failure = ex };
        }
        catch (Exception ex)
        {
            Log.Error("apply: failed", ex, new { path });
            // Not cancellable, so it cannot throw out of this handler; still bounded.
            return new(row) { Written = written, Failure = ex, After = await _visibility.InspectAsync(path, CancellationToken.None) };
        }
    }

    private sealed record ApplyAttempt(PathRowViewModel Row)
    {
        public PathInspection? Before { get; init; }
        public bool Written { get; init; }
        public PathInspection? After { get; init; }
        public Exception? Failure { get; init; }
        public bool Cancelled { get; init; }
    }

    private readonly record struct ApplyOutcome(int Applied, IReadOnlyDictionary<string, ApplyProblem> Problems)
    {
        public static ApplyOutcome Empty { get; } = new(0, new Dictionary<string, ApplyProblem>());
        public IEnumerable<string> ProblemPaths => Problems.Keys;
        public int Unchanged => Count(ApplyProblem.Unchanged);
        public int Missing => Count(ApplyProblem.Missing);
        public int Errors => Count(ApplyProblem.Error);
        public int Unresponsive => Count(ApplyProblem.Unresponsive);
        public int Cancelled => Count(ApplyProblem.Cancelled);
        public int ElevationBusy => Count(ApplyProblem.ElevationBusy);
        public int ElevationDeclined => Count(ApplyProblem.ElevationDeclined);
        private int Count(ApplyProblem key) => Problems.Values.Count(value => value == key);
        public bool HasProblems => Problems.Count > 0;
        public bool HasFailures => Problems.Values.Any(problem => problem.IsFailure());
        public Message Summary => OperationalResultViewModel.VisibilitySummary(Problems, Applied);
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

    private void ShowApplyOutcome(ApplyOutcome outcome, IEnumerable<string> attemptedPaths)
    {
        var attempted = attemptedPaths.ToHashSet(StringComparer.Ordinal);
        ResolveVisibilityResults(attempted.Contains);

        if (outcome.HasProblems)
        {
            var result = new OperationalResultViewModel(OperationalResultOwner.Visibility, outcome.Summary, outcome.HasFailures);
            foreach (var (path, category) in outcome.Problems)
                result.UnresolvedPaths.Add(path, category);
            OperationalResults.Add(result);
            Log.Info("visibility result", new { outcome.Summary });
        }
        OnPropertyChanged(nameof(HasOperationalResults));
    }

    private void ResolveVisibilityResults(Func<string, bool> resolved)
    {
        foreach (var result in OperationalResults.Where(item => item.Owner == OperationalResultOwner.Visibility).ToList())
        {
            var removing = result.UnresolvedPaths.Keys.Where(resolved).ToList();
            if (removing.Count == 0)
                continue;
            foreach (var path in removing)
                result.UnresolvedPaths.Remove(path);
            if (result.UnresolvedPaths.Count == 0)
                OperationalResults.Remove(result);
            else
                result.UpdateVisibilitySummary();
        }
        OnPropertyChanged(nameof(HasOperationalResults));
    }

    private void ShowOperationalResult(OperationalResultOwner owner, Message message, bool error)
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
