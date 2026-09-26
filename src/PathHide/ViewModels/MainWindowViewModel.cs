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
using PathHide.I18n;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;

namespace PathHide.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IJsonStore<List<PathEntry>> _pathListStore;
    private readonly IJsonStore<AppSettings> _settingsStore;
    private readonly BoundedVisibility _visibility;
    private readonly PathScanner _scanner;

    // The same AppSettings instance the Windows visibility service closes over (wired in
    // App's composition root). Mutate its fields in place; never reassign the reference,
    // or the service would read stale state.
    private readonly AppSettings _settings;

    private List<PathEntry> _entries = [];
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _applyCts;

    // Serializes every settings write with the publish that follows it. A Settings save runs on a
    // worker thread and the window-placement save runs on the UI thread as the window closes, and
    // each builds its candidate from the live settings: unserialized, the later write would put back
    // what the earlier one had just changed. A plain lock, because neither holder ever waits for the
    // UI thread while holding it.
    private readonly object _settingsWrite = new();
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
    public Func<Message, Message, Task>? ShowNoticeAsync { get; set; }

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

    public ObservableCollection<OperationalResultViewModel> OperationalResults { get; } = [];

    public bool HasOperationalResults => OperationalResults.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPathAddResult))]
    private PathAddResultViewModel? _pathAddResult;

    private readonly List<string> _pathAddIssuePaths = [];
    private bool _pathAddHasOpaqueIssues;
    private bool _pathAddResultIsPickerFailure;

    public bool HasPathAddResult => PathAddResult is not null;

    // Settings are always available now: the UI font is a cross-platform setting, so the dialog

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

    /// <summary>The saved language preference — a tag or System — used to seed the settings dialog.</summary>
    public string Language => Languages.NormalizePreference(_settings.Language);

    /// <summary>
    /// The computer's own languages, in order, as <c>LanguageBootstrap</c> read them at launch. A
    /// language saved in Settings resolves System against this same list, so System cannot mean one
    /// language at launch and another after a Save.
    /// </summary>
    internal IReadOnlyList<string> ComputerLanguages { get; init; } = [];
    public int? WindowPositionX => _settings.WindowPositionX;
    public int? WindowPositionY => _settings.WindowPositionY;
    public double? WindowWidth => _settings.WindowWidth;
    public double? WindowHeight => _settings.WindowHeight;
    public bool WindowMaximized => _settings.WindowMaximized;

    public string ProgressText => ScanTotal > 0
        ? Localizer.T("status.scanning", ("done", ScanProgress), ("total", ScanTotal))
        : string.Empty;

    public string StatusBarText => Localizer.Of(Summary());

    /// <summary>
    /// The status bar's line: the entry count and each non-zero state, each counted in its own
    /// sentence so it takes its own plural form, joined as the language joins them.
    /// </summary>
    internal Message Summary()
    {
        if (Rows.Count == 0)
            return Message.Of("status.empty");

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
    /// including the already-loaded <paramref name="settings"/>. The Windows
    /// visibility service behind <paramref name="visibility"/> closes over that same
    /// instance to read the current hide mode, so the view model mutates it in place
    /// rather than replacing it.
    /// </summary>
    /// <remarks>
    /// Construction is side-effect-free: no disk I/O and no scan happen here, so the
    /// type is safe to instantiate outside a running app. Call <see cref="Initialize"/>
    /// once the view is ready to load entries and start scanning.
    /// </remarks>
    public MainWindowViewModel(
        BoundedVisibility visibility,
        IJsonStore<List<PathEntry>> pathListStore,
        IJsonStore<AppSettings> settingsStore,
        AppSettings settings)
    {
        _visibility = visibility;
        _pathListStore = pathListStore;
        _settingsStore = settingsStore;
        _settings = settings;
        _scanner = new PathScanner(visibility);
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

    public void ReportLogRevealFailure() =>
        ShowOperationalResult(
            OperationalResultOwner.LogReveal,
            FailurePresentation.LogReveal(),
            error: true);

    public void ResolveLogRevealFailure() =>
        ResolveOperationalResult(OperationalResultOwner.LogReveal);

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

        foreach (var candidate in await ResolveIdentitiesAsync(accepted, token))
        {
            if (updated.Any(e => PathNormalizer.AreEqual(e.Path, candidate)))
            {
                duplicates++;
                duplicatePaths.Add(candidate);
                continue;
            }

            updated.Add(new PathEntry
            {
                Path = candidate,
                DesiredVisibility = DesiredVisibility.Hidden,
            });
            addedPaths.Add(candidate);
            added++;
        }

        Log.Info("add paths", new { added, duplicates, invalid });

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
            ClearPathAddResultIfResolvedBy(addedPaths);
        }
    }));

    /// <summary>
    /// Decides each new path's identity: its parent directory's aliases resolved, so two spellings of
    /// one file become one entry. Done once, here, through the bounded owner, so every later
    /// comparison is a pure string match. A parent that does not answer, cannot be resolved, or is
    /// reached after a cancel keeps the spelling it was given.
    /// </summary>
    private async Task<List<string>> ResolveIdentitiesAsync(List<string> paths, CancellationToken token)
    {
        // One resolution per parent: a batch dropped from one folder asks once.
        var resolvedParents = new Dictionary<string, string?>(StringComparer.Ordinal);
        var identities = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            var parent = PathNormalizer.IdentityParent(path);
            if (parent is not null && !resolvedParents.ContainsKey(parent) && !token.IsCancellationRequested)
            {
                try
                {
                    resolvedParents[parent] = await _visibility.ResolveDirectoryAsync(parent, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Log.Info("add: cancelled while resolving paths");
                }
            }

            identities.Add(parent is not null && resolvedParents.GetValueOrDefault(parent) is { } resolved
                ? PathNormalizer.Rebase(path, resolved)
                : path);
        }
        return identities;
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

        if (parts.Count == 0)
            return;

        var severity = outcome.HasFailures
            ? PathAddResultSeverity.Error
            : invalid > 0 || outcome.Unchanged > 0 || outcome.Missing > 0 || outcome.Cancelled > 0
                ? PathAddResultSeverity.Warning
                : PathAddResultSeverity.Information;

        SetPathAddResult(
            Message.Of("result.summary", "parts", Message.Join("result.join", parts)),
            severity,
            issuePaths: duplicatePaths.Concat(outcome.ProblemPaths),
            hasOpaqueIssues: invalid > 0);
    }

    private void SetPathAddResult(
        Message message,
        PathAddResultSeverity severity,
        IEnumerable<string>? issuePaths = null,
        bool hasOpaqueIssues = false)
    {
        _pathAddResultIsPickerFailure = false;
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
        _pathAddResultIsPickerFailure = false;
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
                Message.Of("remove.title"),
                Message.Of("remove.message", ("count", selected.Count)),
                "common.remove"));

            if (!confirmed)
                return;
        }

        var removing = new HashSet<PathEntry>(selected.Select(row => row.Entry));
        var updated = _entries.Where(entry => !removing.Contains(entry)).ToList();

        Log.Info("remove paths", new { removed = selected.Count });

        var saveFailure = await TrySaveEntriesAsync(updated);
        if (saveFailure is not null)
        {
            ShowOperationalResult(OperationalResultOwner.PathStore, saveFailure, error: true);
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
    // pause-then-resume: it always ends by starting a fresh background scan of the reloaded
    // list, rather than putting back the one it interrupted. That scan runs after the gate is
    // released, like every other, so the next command pauses it instead of queueing behind it.
    private Task ReloadAsync() => UnderMutationGateAsync(async () =>
    {
        await PauseScanningAsync();

        // Reload reconciles the path list and re-scans. It deliberately does NOT reload
        // settings: the settings dialog saves before publishing its complete draft, so the
        // in-memory value never diverges from disk. Copying
        // a freshly loaded settings object back into the shared instance field-by-field would
        // be both brittle (it silently couples to AppSettings having one field) and pointless.
        Log.Info("reload");
        // Off the UI thread: the data folder may sit on a slow or redirected profile share.
        var reloaded = await Task.Run(_pathListStore.Load);
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

        StartBackgroundScan();
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

    /// <summary>
    /// Saves the complete Settings draft atomically, off the UI thread, and publishes it only after
    /// disk agrees. Returns what to tell the reader when the save failed, or null when it landed.
    /// </summary>
    public async Task<Message?> TryApplySettingsAsync(
        string language, string family, bool hiddenAndSystem, ThemePreference theme)
    {
        language = Languages.NormalizePreference(language);
        family = UiFontFamilyValue.Normalize(family);
        var newMode = hiddenAndSystem ? WindowsHideMode.HiddenAndSystem : WindowsHideMode.HiddenOnly;

        AppSettings? previous;
        try
        {
            previous = await Task.Run(() => CommitSettings(candidate =>
            {
                candidate.Language = language;
                candidate.UiFontFamily = family;
                candidate.WindowsHideMode = newMode;
                candidate.Theme = theme;
            }));
        }
        catch (Exception ex)
        {
            Log.Error("settings: save failed", ex);
            return FailurePresentation.SettingsSave(ex);
        }

        if (previous is null)
            return null;

        if (previous.UiFontFamily != family)
        {
            ApplyUiFont();
            OnPropertyChanged(nameof(UiFontFamily));
        }
        if (previous.WindowsHideMode != newMode)
            OnPropertyChanged(nameof(IsHiddenAndSystem));
        if (previous.Theme != theme)
            OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(Language));
        Log.Info("settings: changed", new { language, family, mode = newMode, theme });

        // Last, once disk agrees: a language change redraws everything already on screen.
        Localizer.Use(language, ComputerLanguages);
        return null;
    }

    /// <summary>
    /// Saves the window placement. Synchronous on purpose: it runs as the window closes, and the
    /// write must finish before the process exits — on a worker thread it would be cut off, and
    /// the placement lost, when the app quits.
    /// </summary>
    public void SaveWindowPlacement(int x, int y, double width, double height, bool maximized) =>
        CommitSettings(candidate =>
        {
            candidate.WindowPositionX = x;
            candidate.WindowPositionY = y;
            candidate.WindowWidth = width;
            candidate.WindowHeight = height;
            candidate.WindowMaximized = maximized;
        });

    /// <summary>
    /// Applies <paramref name="change"/> to a copy of the live settings, saves the copy, and only then
    /// copies it into the live instance (which the Windows visibility service reads). Returns the
    /// settings as they were before, or null when the change left them as they were, in which case
    /// nothing is written. A failed save throws and leaves the live settings untouched.
    /// </summary>
    private AppSettings? CommitSettings(Action<AppSettings> change)
    {
        lock (_settingsWrite)
        {
            var previous = CopySettings();
            var candidate = CopySettings();
            change(candidate);
            if (SameSettings(previous, candidate))
                return null;

            _settingsStore.Save(candidate);

            _settings.Language = candidate.Language;
            _settings.UiFontFamily = candidate.UiFontFamily;
            _settings.WindowsHideMode = candidate.WindowsHideMode;
            _settings.Theme = candidate.Theme;
            _settings.WindowPositionX = candidate.WindowPositionX;
            _settings.WindowPositionY = candidate.WindowPositionY;
            _settings.WindowWidth = candidate.WindowWidth;
            _settings.WindowHeight = candidate.WindowHeight;
            _settings.WindowMaximized = candidate.WindowMaximized;
            return previous;
        }
    }

    private static bool SameSettings(AppSettings a, AppSettings b) =>
        Languages.NormalizePreference(a.Language) == Languages.NormalizePreference(b.Language)
        && a.UiFontFamily == b.UiFontFamily
        && a.WindowsHideMode == b.WindowsHideMode
        && a.Theme == b.Theme
        && a.WindowPositionX == b.WindowPositionX
        && a.WindowPositionY == b.WindowPositionY
        && a.WindowWidth == b.WindowWidth
        && a.WindowHeight == b.WindowHeight
        && a.WindowMaximized == b.WindowMaximized;

    private AppSettings CopySettings() => new()
    {
        Language = _settings.Language,
        UiFontFamily = _settings.UiFontFamily,
        Theme = _settings.Theme,
        WindowsHideMode = _settings.WindowsHideMode,
        WindowPositionX = _settings.WindowPositionX,
        WindowPositionY = _settings.WindowPositionY,
        WindowWidth = _settings.WindowWidth,
        WindowHeight = _settings.WindowHeight,
        WindowMaximized = _settings.WindowMaximized,
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

    // --- Internals ---

    /// <summary>
    /// Persists <paramref name="updated"/> off the UI thread and, only if the save lands, makes it the
    /// live entry list and re-syncs the rows. Returns what to tell the user when the save failed,
    /// having changed nothing, or null when it landed.
    /// </summary>
    /// <remarks>
    /// Commit after save, never mutate-then-roll-back. The previous shape deep-cloned the list,
    /// applied the change to live state, and restored the clone when the save threw — so the
    /// correctness of every mutating command rested on each one remembering to snapshot at the
    /// right moment. Building the new list as a value makes a failed save a no-op by
    /// construction: nothing in memory moves until disk agrees.
    /// <para>The save is a write-then-rename plus a backup-store transaction that may wait seconds
    /// for SQLite's write lock, and the data folder may sit on a redirected profile share, so it
    /// never runs on the UI thread. Every caller holds the mutation gate, so saves stay ordered.</para>
    /// </remarks>
    private async Task<Message?> TrySaveEntriesAsync(List<PathEntry> updated)
    {
        // Sort a snapshot so paths.json is diff-stable without imposing that order on the
        // live list. UI ordering is a separate concern handled by the DataGrid's own sort.
        var snapshot = updated.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            await Task.Run(() => _pathListStore.Save(snapshot));
        }
        catch (Exception ex)
        {
            Log.Error("paths: save failed", ex);
            return FailurePresentation.PathListSave(ex);
        }

        _entries = updated;
        SyncRowsWithEntries();
        return null;
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
            ShowOperationalResult(OperationalResultOwner.Scan, FailurePresentation.Scan(ex), error: true);
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
        Log.Info("apply: start", new { count = targets.Count });

        var applied = 0;
        var unchanged = 0;
        var missing = 0;
        var errors = 0;
        var unresponsive = 0;
        var cancelled = 0;
        var problemPaths = new List<string>();
        var retryBucket = new List<PathRowViewModel>();

        void MarkUnresponsive(PathRowViewModel row)
        {
            unresponsive++;
            problemPaths.Add(row.Path);
            row.ActualState = ActualState.Unresponsive;
        }

        for (var index = 0; index < targets.Count; index++)
        {
            var row = targets[index];
            var written = false;
            try
            {
                var inspection = await _visibility.InspectAsync(row.Path, token);

                if (inspection.ActualState == ActualState.Unresponsive)
                {
                    MarkUnresponsive(row);
                    continue;
                }

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

                written = true;
                await _visibility.WriteAsync(row.Path, row.Entry.DesiredVisibility, token);

                var updated = await _visibility.InspectAsync(row.Path, token);
                if (updated.ActualState == ActualState.Unresponsive)
                {
                    MarkUnresponsive(row);
                    continue;
                }
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
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // A path whose write was under way may or may not have changed — the call was
                // abandoned, not undone — so its row no longer claims a state. The rest were
                // never written.
                if (written)
                    row.ActualState = ActualState.Unknown;
                var skipped = targets.Skip(index).ToList();
                cancelled += skipped.Count;
                problemPaths.AddRange(skipped.Select(r => r.Path));
                Log.Info("apply: cancelled", new { skipped = skipped.Count });
                break;
            }
            catch (TimeoutException)
            {
                MarkUnresponsive(row);
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
                // Not cancellable, so it cannot throw out of this handler; still bounded.
                var recheck = await _visibility.InspectAsync(row.Path, CancellationToken.None);
                row.ApplyScanResult(recheck, row.PathFamily);
            }
        }

        int? elevationExitCode = null;

        // A cancelled apply does not go on to raise an elevation prompt: the retry rows are
        // counted as cancelled, untouched.
        if (retryBucket.Count > 0 && token.IsCancellationRequested)
        {
            cancelled += retryBucket.Count;
            problemPaths.AddRange(retryBucket.Select(r => r.Path));
        }
        // retryBucket is only ever populated on Windows (the catch above is filtered to
        // Windows), so this platform check is logically redundant — but it is REQUIRED, not
        // documentary: it is the guard the CA1416 analyzer needs to permit the
        // [SupportedOSPlatform("windows")] call to ApplyAsync below. Do not remove it.
        else if (retryBucket.Count > 0 && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
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
                var recheck = await _visibility.InspectAsync(row.Path, CancellationToken.None);
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
        Log.Info("apply: done", new { applied, unchanged, missing, errors, unresponsive, cancelled, elevationExitCode });
        OnPropertyChanged(nameof(StatusBarText));

        return new ApplyOutcome(applied, unchanged, missing, errors, unresponsive, cancelled, problemPaths);
    }

    private readonly record struct ApplyOutcome(
        int Applied,
        int Unchanged,
        int Missing,
        int Errors,
        int Unresponsive,
        int Cancelled,
        IReadOnlyList<string> ProblemPaths)
    {
        public static ApplyOutcome Empty { get; } = new(0, 0, 0, 0, 0, 0, []);

        public bool HasProblems => Unchanged > 0 || Missing > 0 || Errors > 0 || Unresponsive > 0 || Cancelled > 0;

        /// <summary>A path that failed or never answered; either is shown as an error.</summary>
        public bool HasFailures => Errors > 0 || Unresponsive > 0;

        /// <summary>
        /// The counts, each in its own sentence, joined as the language joins them. Only shown when
        /// something went wrong (<see cref="ShowApplyOutcome"/>), so there is always a count to show.
        /// </summary>
        public Message Summary
        {
            get
            {
                var parts = new List<Message>();
                if (Applied > 0) parts.Add(Message.Of("apply.applied", ("count", Applied)));
                if (Unchanged > 0) parts.Add(Message.Of("apply.unchanged", ("count", Unchanged)));
                if (Missing > 0) parts.Add(Message.Of("apply.missing", ("count", Missing)));
                if (Errors > 0) parts.Add(Message.Of("apply.errors", ("count", Errors)));
                if (Unresponsive > 0) parts.Add(Message.Of("apply.unresponsive", ("count", Unresponsive)));
                if (Cancelled > 0) parts.Add(Message.Of("apply.cancelled", ("count", Cancelled)));
                return Message.Join("apply.join", parts);
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
        if (outcome.HasFailures)
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
