using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PathHide.Services;
using PathHide.Storage;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using PathHide.Tests.I18n;
using Xunit;
using static PathHide.Tests.ViewModels.RecordFormatTests;

namespace PathHide.Tests.ViewModels;

/// <summary>
/// The records window's reading, paging, filtering and live updates, on the UI thread the window runs
/// them on, against a reader whose answers the test gives.
/// </summary>
[Collection(ProcessLogCollection.Name)]
public sealed class RecordsViewModelTests
{
    private const string ThisLaunch = "2026-10-04T08:00:00.000Z";

    private readonly FakeRecordsReader _reader = new();
    private readonly FakeDelays _delays = new();

    private RecordsViewModel Started()
    {
        var vm = new RecordsViewModel(_reader, ThisLaunch, _delays.Delay);
        vm.Start();
        Settle();
        return vm;
    }

    private static void Settle() => Dispatcher.UIThread.RunJobs();

    private static RecordSummary[] Newest(long from, int count) =>
        Enumerable.Range(0, count).Select(offset => Record(from - offset)).ToArray();

    private void Answer(bool more, params RecordSummary[] records)
    {
        _reader.Unanswered.Single().Answer(more, records);
        Settle();
    }

    private void Signal(RecordsViewModel vm, int times = 1)
    {
        for (var i = 0; i < times; i++)
            vm.OnRecordStored();
        Settle();
    }

    private void ElapseLive()
    {
        _delays.Elapse(RecordsViewModel.LiveInterval);
        Settle();
    }

    // Reads run one at a time, and a new selection cancels the detail read for the old one.
    [AvaloniaFact]
    public void An_older_A_request_cannot_replace_the_current_A_detail_or_its_loading_note()
    {
        var pending = new List<TaskCompletionSource<RecordDetail?>>();
        _reader.DetailRead = (_, token) =>
        {
            var reply = new TaskCompletionSource<RecordDetail?>();
            token.Register(() => reply.TrySetCanceled(token));
            pending.Add(reply);
            return reply.Task;
        };
        var vm = Started();
        Answer(false, Record(2), Record(1));
        vm.SelectedRow = vm.Rows[1];
        Settle();
        vm.SelectedRow = vm.Rows[0];
        vm.SelectedRow = vm.Rows[1];
        Settle();
        Assert.Equal(English.Of("records.loading"), vm.DetailNote);
        Assert.All(pending[..^1], reply => Assert.True(reply.Task.IsCanceled));
        Assert.Equal(RecordDetailStatus.Loading, vm.DetailStatus);
        pending[^1].SetResult(new RecordDetail(1, ThisLaunch, Record(1).Time, "info", "current A", null, null));
        Settle();
        Assert.Equal("current A", vm.Detail!.Message);
        Assert.Equal(RecordDetailStatus.Ready, vm.DetailStatus);
        vm.Close();
    }

    [AvaloniaFact]
    public void A_late_import_with_an_older_time_appears_inside_the_loaded_range()
    {
        var vm = Started();
        Answer(true, Newest(300, 100));
        vm.LoadMore();
        Settle();
        Answer(false, Newest(200, 100));
        var selected = vm.Rows.Single(row => row.Id == 150);
        vm.SelectedRow = selected;

        Signal(vm);
        ElapseLive();
        Assert.Equal(300, _reader.LastPage.Query.InsertedAfter);
        Assert.Null(_reader.LastPage.Query.After);
        var imported = Record(1000) with { Time = Record(150).Time };
        Answer(false, imported);

        Assert.Contains(vm.Rows, row => row.Id == 1000);
        Assert.Same(selected, vm.SelectedRow);
        Assert.Equal(201, vm.Rows.Count);
        Assert.False(vm.HasMore);
    }

    [AvaloniaFact]
    public void A_live_burst_bridges_to_the_old_list_without_losing_selection_or_the_middle_records()
    {
        var vm = Started();
        Answer(false, Newest(100, 100));
        var selected = vm.Rows[20];
        vm.SelectedRow = selected;
        Signal(vm);
        ElapseLive();
        Answer(true, Enumerable.Range(101, 100).Select(i => Record(i)).ToArray());
        Assert.Equal(200, _reader.LastPage.Query.InsertedAfter);
        Answer(false, Enumerable.Range(201, 50).Select(i => Record(i)).ToArray());

        Assert.Equal(Newest(250, 250).Select(record => record.Id), vm.Rows.Select(row => row.Id));
        Assert.Same(selected, vm.SelectedRow);
        Assert.False(vm.HasMore);
    }

    [AvaloniaFact]
    public void A_filter_change_discards_an_unfinished_live_bridge()
    {
        var vm = Started();
        Answer(true, Newest(100, 100));
        Signal(vm);
        ElapseLive();
        Answer(true, Newest(250, 100));
        var bridge = _reader.LastPage;
        vm.SelectedLevel = vm.LevelOptions.Single(option => option.Level == RecordLevelFilter.Error);
        Settle();
        _reader.LastPage.Answer(false, Record(300, level: "error"));
        Settle();
        bridge.Answer(true, Newest(150, 100));
        Settle();

        Assert.Equal([300], vm.Rows.Select(row => row.Id));
        Assert.False(vm.HasMore);
    }

    [AvaloniaFact]
    public void It_shows_a_loading_note_while_the_first_page_is_read_then_the_rows_newest_first_with_nothing_selected()
    {
        var vm = Started();

        Assert.Equal(RecordsListStatus.Loading, vm.ListStatus);
        Assert.Equal(English.Of("records.loading"), vm.ListNote);
        Assert.Empty(vm.Rows);

        Answer(false, Record(3), Record(2), Record(1));

        Assert.Equal(RecordsListStatus.Ready, vm.ListStatus);
        Assert.Null(vm.ListNote);
        Assert.Equal([3, 2, 1], vm.Rows.Select(row => row.Id));
        Assert.Null(vm.SelectedRow);
        Assert.Equal(English.Of("records.noSelection"), vm.DetailNote);
    }

    [AvaloniaFact]
    public void Every_filter_is_off_when_it_opens_and_warnings_and_errors_comes_first_among_the_levels()
    {
        var vm = Started();

        Assert.Equal(new RecordsQuery(null, null, "", null), _reader.LastPage.Query);
        Assert.Null(vm.SelectedLevel.Level);
        Assert.Null(vm.SelectedLaunch.Session);
        Assert.Equal(
            [null, RecordLevelFilter.WarningsAndErrors, RecordLevelFilter.Error, RecordLevelFilter.Warn, RecordLevelFilter.Info, RecordLevelFilter.Debug],
            vm.LevelOptions.Select(option => option.Level));
        Assert.Equal(["All levels", "Warnings and errors", "Error", "Warning", "Info", "Debug"], vm.LevelOptions.Select(option => option.Label));
    }

    [AvaloniaFact]
    public void It_lists_every_launch_newest_first_and_names_this_one()
    {
        _reader.Sessions = [ThisLaunch, "2026-10-03T08:00:00.000Z"];
        var vm = Started();

        Assert.Equal([null, ThisLaunch, "2026-10-03T08:00:00.000Z"], vm.LaunchOptions.Select(option => option.Session));
        Assert.Equal(English.Of("records.allLaunches"), vm.LaunchOptions[0].Label);
        Assert.EndsWith("(this launch)", vm.LaunchOptions[1].Label, StringComparison.Ordinal);
        Assert.DoesNotContain("this launch", vm.LaunchOptions[2].Label, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Each_filter_reads_again_and_the_search_applies_once_typing_pauses()
    {
        _reader.Sessions = [ThisLaunch];
        var vm = Started();
        Answer(false, Record(1));

        vm.SelectedLevel = vm.LevelOptions.Single(option => option.Level == RecordLevelFilter.WarningsAndErrors);
        Settle();
        Assert.Equal(RecordLevelFilter.WarningsAndErrors, _reader.LastPage.Query.Level);
        Answer(false);

        vm.SelectedLaunch = vm.LaunchOptions[1];
        Settle();
        Assert.Equal(ThisLaunch, _reader.LastPage.Query.Session);
        Answer(false);
        var reads = _reader.Pages.Count;

        vm.SearchText = "s";
        vm.SearchText = "sa";
        vm.SearchText = "save";
        Settle();
        Assert.Equal(reads, _reader.Pages.Count);

        _delays.Elapse(RecordsViewModel.SearchDelay);
        Settle();

        Assert.Equal(reads + 1, _reader.Pages.Count);
        Assert.Equal(new RecordsQuery(ThisLaunch, RecordLevelFilter.WarningsAndErrors, "save", null), _reader.LastPage.Query);
    }

    [AvaloniaFact]
    public void A_filter_with_no_matches_says_so_and_a_record_that_arrives_clears_it()
    {
        var vm = Started();
        Answer(false, Record(1));

        vm.SelectedLevel = vm.LevelOptions.Single(option => option.Level == RecordLevelFilter.Error);
        Settle();
        Answer(false);
        Assert.Empty(vm.Rows);
        Assert.Equal(English.Of("records.empty"), vm.ListNote);

        Signal(vm);
        ElapseLive();
        Answer(false, Record(2, level: "error"));

        Assert.Equal([2], vm.Rows.Select(row => row.Id));
        Assert.Null(vm.ListNote);
    }

    [AvaloniaFact]
    public void The_next_page_is_read_from_the_last_row_once_and_only_once_at_a_time()
    {
        var vm = Started();
        Answer(true, Newest(300, RecordsReader.PageSize));

        vm.OnListScrolled(atTop: false, nearEnd: true);
        vm.OnListScrolled(atTop: false, nearEnd: true);
        vm.LoadMore();
        Settle();

        var next = Assert.Single(_reader.Unanswered);
        Assert.Equal(new RecordCursor(Record(201).Time, 201), next.Query.After);
        Assert.True(vm.IsLoadingMore);
        Assert.Equal(English.Of("records.loading"), vm.MoreNote);

        Answer(false, Newest(200, 3));

        Assert.Equal(RecordsReader.PageSize + 3, vm.Rows.Count);
        Assert.False(vm.HasMore);
        Assert.Null(vm.MoreNote);

        vm.LoadMore();
        Settle();
        Assert.Empty(_reader.Unanswered);
    }

    [AvaloniaFact]
    public void A_page_that_leaves_the_list_short_reads_the_next_by_itself()
    {
        var vm = Started();
        var applied = 0;
        vm.PageApplied += () => applied++;
        Answer(true, Newest(10, 5));
        Assert.Equal(1, applied);

        vm.CheckFill(nearEnd: true);
        Settle();

        Assert.NotNull(Assert.Single(_reader.Unanswered).Query.After);
    }

    [AvaloniaFact]
    public void A_failed_page_keeps_its_note_at_the_end_and_is_read_again_when_the_end_is_reached_again()
    {
        var vm = Started();
        Answer(true, Newest(300, RecordsReader.PageSize));

        vm.LoadMore();
        Settle();
        _reader.Unanswered.Single().Fail();
        Settle();

        Assert.True(vm.MoreFailed);
        Assert.Equal(English.Of("records.loadFailed"), vm.MoreNote);
        Assert.Equal(RecordsReader.PageSize, vm.Rows.Count);

        // A page laid out after the failure does not retry by itself; reaching the end does.
        vm.CheckFill(nearEnd: true);
        Settle();
        Assert.Empty(_reader.Unanswered);

        vm.OnListScrolled(atTop: false, nearEnd: true);
        Settle();
        Assert.False(vm.MoreFailed);
        Answer(false, Newest(200, 1));
        Assert.Equal(RecordsReader.PageSize + 1, vm.Rows.Count);
        Assert.Null(vm.MoreNote);
    }

    [AvaloniaFact]
    public void Signals_during_initial_read_are_consumed_after_its_snapshot()
    {
        var vm = Started();
        Signal(vm);
        ElapseLive();
        Assert.Single(_reader.Unanswered);
        Answer(false, Record(2));
        Assert.Equal(2, _reader.LastPage.Query.InsertedAfter);
        Answer(false, Record(3));
        Assert.Equal([3, 2], vm.Rows.Select(row => row.Id));
    }

    [AvaloniaFact]
    public void A_burst_of_signals_refreshes_the_loaded_range_once_while_at_the_top_keeping_the_rows_shown()
    {
        var vm = Started();
        Answer(true, Newest(300, RecordsReader.PageSize));
        vm.LoadMore();
        Settle();
        Answer(true, Newest(200, RecordsReader.PageSize));
        var reads = _reader.Pages.Count;
        var sessionReads = _reader.SessionReads;

        Signal(vm, times: 5);
        Assert.Equal(reads, _reader.Pages.Count);
        Assert.Equal(1, _delays.Running(RecordsViewModel.LiveInterval));

        ElapseLive();
        var newest = _reader.Unanswered.Single();
        Assert.Null(newest.Query.After);
        // The rows stay on screen while the newest page is read.
        Assert.Equal(RecordsListStatus.Ready, vm.ListStatus);
        Assert.Equal(2 * RecordsReader.PageSize, vm.Rows.Count);

        Assert.Equal(300, newest.Query.InsertedAfter);
        Answer(false, Record(301), Record(302));

        Assert.Equal(reads + 1, _reader.Pages.Count);
        // Only this launch gains records while the window is open, so the launches are not read again.
        Assert.Equal(sessionReads, _reader.SessionReads);
        Assert.Equal([302, 301, 300], vm.Rows.Take(3).Select(row => row.Id));
        Assert.Equal(2 * RecordsReader.PageSize + 2, vm.Rows.Count);
        Assert.True(vm.HasMore);
    }

    [AvaloniaFact]
    public void While_scrolled_down_new_records_wait_until_the_list_is_back_at_the_top()
    {
        var vm = Started();
        Answer(true, Newest(300, RecordsReader.PageSize));
        vm.OnListScrolled(atTop: false, nearEnd: false);

        Signal(vm);
        ElapseLive();
        Assert.Empty(_reader.Unanswered);
        Assert.Equal(300, vm.Rows[0].Id);

        vm.OnListScrolled(atTop: true, nearEnd: false);
        Settle();
        Answer(false, Record(301));

        Assert.Equal(301, vm.Rows[0].Id);
    }

    // A read for new records that was under way when the user scrolled down applies nothing, so the list
    // does not move under the reader; the records join once it is back at the top.
    [AvaloniaFact]
    public void New_records_read_before_scrolling_down_wait_for_the_top()
    {
        var vm = Started();
        Answer(true, Newest(300, RecordsReader.PageSize));
        Signal(vm);
        ElapseLive();
        var newest = _reader.Unanswered.Single();

        vm.OnListScrolled(atTop: false, nearEnd: false);
        newest.Answer(false, Record(301));
        Settle();
        Assert.Equal(300, vm.Rows[0].Id);
        Assert.Empty(_reader.Unanswered);

        vm.OnListScrolled(atTop: true, nearEnd: false);
        Settle();
        Answer(false, Record(301));
        Assert.Equal(301, vm.Rows[0].Id);
    }

    [AvaloniaFact]
    public void The_selected_record_stays_selected_through_an_update()
    {
        _reader.Details[2] = new RecordDetail(2, ThisLaunch, Record(2).Time, "warn", "m2", "{\"a\":1}", null);
        var vm = Started();
        Answer(false, Record(2), Record(1));
        var selected = vm.Rows[0];
        vm.SelectedRow = selected;
        Settle();
        Assert.Equal(RecordDetailStatus.Ready, vm.DetailStatus);

        Signal(vm);
        ElapseLive();
        Answer(false, Record(3), Record(2), Record(1));

        Assert.Same(selected, vm.SelectedRow);
        Assert.Same(selected, vm.Rows[1]);
        Assert.Equal("m2", vm.Detail!.Message);
        Assert.Equal("a: 1", vm.Detail.FieldsText);
    }

    [AvaloniaFact]
    public void After_a_failed_read_new_record_signals_are_ignored_until_a_read_succeeds()
    {
        var vm = Started();
        _reader.Unanswered.Single().Fail();
        Settle();
        Assert.Equal(RecordsListStatus.Failed, vm.ListStatus);

        // The failure's own log line is a stored record; its signal must not start the next read.
        Signal(vm, times: 3);
        Assert.Equal(0, _delays.Running(RecordsViewModel.LiveInterval));
        Assert.Empty(_reader.Unanswered);

        // A read the reader asks for still runs, and once it succeeds the signals count again.
        vm.SelectedLevel = vm.LevelOptions[1];
        Settle();
        Answer(false, Record(1));
        Signal(vm);
        Assert.Equal(1, _delays.Running(RecordsViewModel.LiveInterval));
    }

    [AvaloniaFact]
    public void A_failed_list_says_so_without_the_raw_error()
    {
        var vm = Started();
        _reader.Unanswered.Single().Fail();
        Settle();

        Assert.True(vm.IsListFailed);
        Assert.Equal(English.Of("records.loadFailed"), vm.ListNote);
        Assert.DoesNotContain("SQLite", vm.ListNote, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void A_record_that_cannot_be_read_says_so_and_an_older_detail_read_never_replaces_a_newer_one()
    {
        _reader.Details[1] = new RecordDetail(1, ThisLaunch, Record(1).Time, "info", "m1", null, null);
        var vm = Started();
        Answer(false, Record(2), Record(1));

        vm.SelectedRow = vm.Rows[0];
        Settle();
        Assert.Equal(RecordDetailStatus.Failed, vm.DetailStatus);
        Assert.Equal(English.Of("records.detailFailed"), vm.DetailNote);

        vm.SelectedRow = vm.Rows[1];
        Settle();
        Assert.Equal("m1", vm.Detail!.Message);
        Assert.False(vm.Detail.HasFields);
        Assert.False(vm.Detail.HasError);
    }

    [AvaloniaFact]
    public void Nothing_read_applies_after_it_closes()
    {
        var vm = Started();
        var first = _reader.LastPage;
        vm.Close();
        Settle();

        Assert.True(first.Token.IsCancellationRequested);
        first.Answer(false, Record(1));
        Signal(vm);

        Assert.Empty(vm.Rows);
        Assert.Equal(0, _delays.Running(RecordsViewModel.LiveInterval));
    }

    [AvaloniaFact]
    public void A_page_read_for_older_filters_is_dropped()
    {
        var vm = Started();
        var first = _reader.LastPage;
        vm.SelectedLevel = vm.LevelOptions[2];
        Settle();

        first.Answer(false, Record(9));
        Settle();
        Assert.Empty(vm.Rows);

        Answer(false, Record(1, level: "error"));
        Assert.Equal([1], vm.Rows.Select(row => row.Id));
    }

    // A read the window no longer needs is cancelled, so the reader stops it inside the database
    // rather than letting a superseded search scan the whole log in the background.
    [AvaloniaFact]
    public void A_superseded_read_is_cancelled_and_closing_cancels_the_rest()
    {
        var vm = Started();
        var first = _reader.Unanswered.Single();

        vm.SelectedLevel = vm.LevelOptions[1];
        Settle();
        var second = _reader.Unanswered.Last();

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(second.Token.IsCancellationRequested);

        vm.Close();
        Assert.True(second.Token.IsCancellationRequested);
    }

    [AvaloniaFact]
    public void A_read_that_succeeds_writes_no_record_and_a_failed_one_writes_one()
    {
        var sink = new CapturingSink();
        Log.Start(sink, Path.Combine(Path.GetTempPath(), "pathhide-records-vm-tests", Guid.NewGuid().ToString("N")));
        try
        {
            _reader.Details[1] = new RecordDetail(1, ThisLaunch, Record(1).Time, "info", "m1", null, null);
            var vm = Started();
            Answer(true, Record(1));
            vm.SelectedRow = vm.Rows[0];
            Settle();
            vm.LoadMore();
            Settle();
            Answer(false);
            Signal(vm);
            ElapseLive();
            Answer(false, Record(1));

            vm.SelectedLevel = vm.LevelOptions[1];
            Settle();
            _reader.Unanswered.Single().Fail();
            Settle();
        }
        finally
        {
            Log.Shutdown();
        }

        Assert.Equal(["records window: a read failed"], sink.Messages);
    }

    private sealed class CapturingSink : ILogSink
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return [.. _messages];
            }
        }

        public void Write(LogEntry entry)
        {
            lock (_messages)
                _messages.Add(entry.Message);
        }

        public void Dispose()
        {
        }
    }
}
