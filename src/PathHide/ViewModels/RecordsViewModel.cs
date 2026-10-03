using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PathHide.I18n;
using PathHide.Services;
using PathHide.Storage;

namespace PathHide.ViewModels;

public enum RecordsListStatus
{
    Loading,
    Failed,
    Ready,
}

public enum RecordDetailStatus
{
    None,
    Loading,
    Failed,
    Ready,
}

/// <summary>
/// The records window: a filtered list of <c>records.sqlite3</c>, newest first, read a page at a time,
/// and the selected record whole. Every method runs on the UI thread; the reads run on the reader's
/// worker, and a result applies only while what it was read for is still what the window asks for
/// (PLAYBOOK, Own the work in flight).
/// </summary>
/// <remarks>
/// Live updates: each record the database stores signals <see cref="OnRecordStored"/>, and the newest
/// page is read again at most once per <see cref="LiveInterval"/>. It joins the list at once while the
/// list is at its top, and otherwise waits until the list is back there, so the list never moves under
/// the reader. A failed read is itself logged as a record, whose signal would start the next read, so
/// signals are ignored after a failure until a read succeeds.
/// </remarks>
public sealed partial class RecordsViewModel : ObservableObject
{
    internal static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);
    internal static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(1);

    private readonly IRecordsReader _reader;
    private readonly string _currentSession;
    private readonly Func<TimeSpan, Task> _delay;

    // The query the list was read for, its After left empty; live reads and later pages use it.
    private RecordsQuery _listQuery;
    // Raised with each new list query, so a page read for an older one is dropped.
    private int _generation;
    // The busy claim for the next page, taken before its read starts.
    private bool _fetchingMore;
    private bool _newestPending;
    private bool _liveSuspended;
    private bool _liveScheduled;
    private bool _atTop = true;
    private bool _closed;
    private int _searchVersion;
    private string _appliedSearch = string.Empty;
    // The record the detail pane was last asked for, so an older detail read is dropped.
    private long? _detailFor;

    /// <param name="reader">Where the records are read.</param>
    /// <param name="currentSession">This launch's session, which the launch filter names as this launch.</param>
    /// <param name="delay">How the search and live intervals wait; tests replace it.</param>
    public RecordsViewModel(IRecordsReader reader, string currentSession, Func<TimeSpan, Task>? delay = null)
    {
        _reader = reader;
        _currentSession = currentSession;
        _delay = delay ?? (wait => Task.Delay(wait));
        LevelOptions =
        [
            new RecordLevelOption(null),
            .. Enum.GetValues<RecordLevelFilter>().Select(level => new RecordLevelOption(level)),
        ];
        LaunchOptions = [new RecordLaunchOption(null, currentSession)];
        // Every filter off when the window opens. Set on the fields, so nothing is read before Start.
        _selectedLevel = LevelOptions[0];
        _selectedLaunch = LaunchOptions[0];
        _listQuery = Query(after: null);
    }

    public ObservableCollection<RecordRowViewModel> Rows { get; } = [];

    public IReadOnlyList<RecordLevelOption> LevelOptions { get; }

    public ObservableCollection<RecordLaunchOption> LaunchOptions { get; }

    [ObservableProperty]
    private RecordLevelOption _selectedLevel;

    [ObservableProperty]
    private RecordLaunchOption _selectedLaunch;

    /// <summary>The search box's text; it applies <see cref="SearchDelay"/> after the last keystroke.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private RecordRowViewModel? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListNote), nameof(HasListNote), nameof(IsListFailed))]
    private RecordsListStatus _listStatus = RecordsListStatus.Loading;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoreNote), nameof(HasMoreNote))]
    private bool _isLoadingMore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoreNote), nameof(HasMoreNote))]
    private bool _moreFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailNote), nameof(HasDetailNote), nameof(IsDetailFailed), nameof(HasDetail))]
    private RecordDetailStatus _detailStatus = RecordDetailStatus.None;

    [ObservableProperty]
    private RecordDetailViewModel? _detail;

    /// <summary>Raised after a page joins the list, so the view can see whether it left the list short.</summary>
    public event Action? PageApplied;

    /// <summary>What the list says in place of rows: loading, a failure, or no matches.</summary>
    public string? ListNote => ListStatus switch
    {
        RecordsListStatus.Loading => Localizer.T("records.loading"),
        RecordsListStatus.Failed => Localizer.T("records.loadFailed"),
        _ when Rows.Count == 0 => Localizer.T("records.empty"),
        _ => null,
    };

    public bool HasListNote => ListNote is not null;

    public bool IsListFailed => ListStatus == RecordsListStatus.Failed;

    /// <summary>What the end of the list says while the next page loads, or after it failed.</summary>
    public string? MoreNote =>
        MoreFailed ? Localizer.T("records.loadFailed")
        : IsLoadingMore ? Localizer.T("records.loading")
        : null;

    public bool HasMoreNote => MoreNote is not null;

    public string? DetailNote => DetailStatus switch
    {
        RecordDetailStatus.None => Localizer.T("records.noSelection"),
        RecordDetailStatus.Failed => Localizer.T("records.detailFailed"),
        _ => null,
    };

    public bool HasDetailNote => DetailNote is not null;

    public bool IsDetailFailed => DetailStatus == RecordDetailStatus.Failed;

    public bool HasDetail => DetailStatus == RecordDetailStatus.Ready;

    /// <summary>A launch as the filter and the detail name it: its start, and whether it is this launch.</summary>
    internal static string LaunchLabel(string session, string currentSession)
    {
        var time = RecordFormat.ListTime(session, Localizer.Current.Culture, TimeZoneInfo.Local);
        return session == currentSession ? Localizer.T("records.thisLaunch", ("time", time)) : time;
    }

    /// <summary>Reads the first page and the launches, as the window opens.</summary>
    public void Start()
    {
        Reload();
        _ = ReadSessionsAsync();
    }

    /// <summary>The window has closed: nothing read from now on applies.</summary>
    public void Close() => _closed = true;

    /// <summary>The list scrolled: whether it is at its top, and whether it is within a screen of its end.</summary>
    public void OnListScrolled(bool atTop, bool nearEnd)
    {
        _atTop = atTop;
        if (_newestPending && atTop)
        {
            _newestPending = false;
            _ = ReadNewestAsync();
        }
        if (nearEnd)
            LoadMore();
    }

    /// <summary>
    /// A page has joined the list and been laid out: one that leaves the list short of its end reads the
    /// next. A failed page waits for the reader to reach the end instead.
    /// </summary>
    public void CheckFill(bool nearEnd)
    {
        if (nearEnd && !IsLoadingMore && !MoreFailed)
            LoadMore();
    }

    /// <summary>
    /// Reads the next page after the last row, unless one is being read or none follows. A failed page
    /// is read again when this is called again.
    /// </summary>
    public void LoadMore() => _ = LoadMoreAsync();

    /// <summary>The database stored a record. Called on the UI thread.</summary>
    public async void OnRecordStored()
    {
        if (_closed || _liveScheduled || _liveSuspended)
            return;

        _liveScheduled = true;
        try
        {
            await _delay(LiveInterval);
        }
        finally
        {
            _liveScheduled = false;
        }

        if (_closed)
            return;
        _ = ReadSessionsAsync();
        if (_atTop)
            _ = ReadNewestAsync();
        else
            _newestPending = true;
    }

    /// <summary>Brings every word the window holds into the current language.</summary>
    internal void Retranslate()
    {
        OnPropertyChanged(string.Empty);
        foreach (var row in Rows)
            row.Retranslate();
        foreach (var option in LevelOptions)
            option.Retranslate();
        foreach (var option in LaunchOptions)
            option.Retranslate();
        Detail?.Retranslate();
    }

    // A select that drops its choice, as one can while its items change, falls back to All.
    partial void OnSelectedLevelChanged(RecordLevelOption value)
    {
        if (value is null)
            SelectedLevel = LevelOptions[0];
        else
            Reload();
    }

    partial void OnSelectedLaunchChanged(RecordLaunchOption value)
    {
        if (value is null)
            SelectedLaunch = LaunchOptions[0];
        else
            Reload();
    }

    partial void OnSearchTextChanged(string value) => _ = ApplySearchAsync();

    partial void OnSelectedRowChanged(RecordRowViewModel? value) => _ = ReadDetailAsync(value?.Id);

    private RecordsQuery Query(RecordCursor? after) =>
        new(SelectedLaunch.Session, SelectedLevel.Level, _appliedSearch, after);

    private async Task ApplySearchAsync()
    {
        var version = ++_searchVersion;
        await _delay(SearchDelay);
        if (_closed || version != _searchVersion || SearchText == _appliedSearch)
            return;

        _appliedSearch = SearchText;
        Reload();
    }

    // The list read again from its first page for the filters as they are now.
    private void Reload() => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        var query = Query(after: null);
        _listQuery = query;
        var generation = ++_generation;
        _fetchingMore = false;
        _newestPending = false;
        var keep = SelectedRow?.Id;
        IsLoadingMore = false;
        MoreFailed = false;
        HasMore = false;
        Rows.Clear();
        ListStatus = RecordsListStatus.Loading;

        RecordsPage page;
        try
        {
            page = await _reader.ReadPageAsync(query);
        }
        catch (Exception ex)
        {
            if (generation != _generation || _closed)
                return;
            Fail("page", ex);
            ListStatus = RecordsListStatus.Failed;
            return;
        }

        if (generation != _generation || _closed)
            return;
        _liveSuspended = false;
        foreach (var record in page.Records)
            Rows.Add(new RecordRowViewModel(record));
        HasMore = page.More;
        ListStatus = RecordsListStatus.Ready;
        // The record selected before the filters changed stays selected while it is still listed.
        if (keep is { } id && Rows.FirstOrDefault(row => row.Id == id) is { } kept)
            SelectedRow = kept;
        PageApplied?.Invoke();
    }

    // The newest page read again for new records. It joins the rows shown rather than replacing them,
    // so the list never falls back to its loading note and the pages already read stay.
    private async Task ReadNewestAsync()
    {
        var generation = _generation;
        RecordsPage page;
        try
        {
            page = await _reader.ReadPageAsync(_listQuery);
        }
        catch (Exception ex)
        {
            if (generation == _generation && !_closed)
                Fail("newest", ex);
            return;
        }

        if (generation != _generation || _closed)
            return;
        _liveSuspended = false;
        if (ListStatus != RecordsListStatus.Ready)
        {
            Rows.Clear();
            foreach (var record in page.Records)
                Rows.Add(new RecordRowViewModel(record));
            HasMore = page.More;
            ListStatus = RecordsListStatus.Ready;
        }
        else
        {
            var (records, more) = RecordFormat.MergeNewestPage(Rows.Select(row => row.Record).ToList(), HasMore, page);
            // The merge keeps every row shown in its order and only adds, so each new record is
            // inserted where it falls; a row object is never replaced, and the selection stays on it.
            for (var index = 0; index < records.Count; index++)
            {
                if (index >= Rows.Count || Rows[index].Id != records[index].Id)
                    Rows.Insert(index, new RecordRowViewModel(records[index]));
            }
            HasMore = more;
            OnPropertyChanged(nameof(ListNote));
            OnPropertyChanged(nameof(HasListNote));
        }
        PageApplied?.Invoke();
    }

    private async Task LoadMoreAsync()
    {
        if (ListStatus != RecordsListStatus.Ready || !HasMore || _fetchingMore)
            return;

        _fetchingMore = true;
        var generation = _generation;
        IsLoadingMore = true;
        MoreFailed = false;
        var query = _listQuery with { After = RecordFormat.CursorAfter(Rows.Select(row => row.Record).ToList()) };

        RecordsPage page;
        try
        {
            page = await _reader.ReadPageAsync(query);
        }
        catch (Exception ex)
        {
            if (generation != _generation || _closed)
                return;
            _fetchingMore = false;
            Fail("next page", ex);
            IsLoadingMore = false;
            MoreFailed = true;
            return;
        }

        if (generation != _generation || _closed)
            return;
        _fetchingMore = false;
        _liveSuspended = false;
        var shown = Rows.Select(row => row.Id).ToHashSet();
        foreach (var record in page.Records.Where(record => !shown.Contains(record.Id)))
            Rows.Add(new RecordRowViewModel(record));
        HasMore = page.More;
        IsLoadingMore = false;
        PageApplied?.Invoke();
    }

    private async Task ReadSessionsAsync()
    {
        IReadOnlyList<string> sessions;
        try
        {
            sessions = await _reader.ReadSessionsAsync();
        }
        catch (Exception ex)
        {
            if (!_closed)
                Fail("launches", ex);
            return;
        }

        if (_closed)
            return;
        // Records are never deleted, so a launch once listed stays; the new ones join in order.
        var listed = LaunchOptions.Skip(1).Select(option => option.Session).ToHashSet();
        var ordered = sessions.OrderByDescending(session => session, StringComparer.Ordinal).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            if (!listed.Contains(ordered[index]))
                LaunchOptions.Insert(Math.Min(index + 1, LaunchOptions.Count), new RecordLaunchOption(ordered[index], _currentSession));
        }
    }

    private async Task ReadDetailAsync(long? id)
    {
        _detailFor = id;
        if (id is not { } recordId)
        {
            Detail = null;
            DetailStatus = RecordDetailStatus.None;
            return;
        }

        DetailStatus = RecordDetailStatus.Loading;
        RecordDetail? record;
        try
        {
            record = await _reader.ReadDetailAsync(recordId);
        }
        catch (Exception ex)
        {
            if (_detailFor != id || _closed)
                return;
            Log.Error("records window: a read failed", ex, new { read = "record", id = recordId });
            Detail = null;
            DetailStatus = RecordDetailStatus.Failed;
            return;
        }

        if (_detailFor != id || _closed)
            return;
        Detail = record is null ? null : new RecordDetailViewModel(record, _currentSession);
        DetailStatus = record is null ? RecordDetailStatus.Failed : RecordDetailStatus.Ready;
    }

    // A failed list read stops live reads until one succeeds: its own log line is a stored record.
    private void Fail(string read, Exception error)
    {
        _liveSuspended = true;
        Log.Error("records window: a read failed", error, new { read });
    }
}
