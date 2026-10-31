using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

/// <summary>
/// The records window's paging and live updates in the real window, over a real records database
/// read through the session log's writer, judged by the scroll position, visible height and content
/// height the list's own scroll viewer reports. The live interval is the test's to end.
/// </summary>
[Collection(ProcessLogCollection.Name)]
public sealed class RecordsWindowScrollTests : WindowTest
{
    private const int Seeded = 10_000;

    private const string SeedSession = "2026-01-01T00:00:00.000Z";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pathhide-records-scroll-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeDelays _delays = new();

    private static MainWindowViewModel Owner()
    {
        var settings = new FakeSettingsStore();
        return new MainWindowViewModel(
            new VisibilityCalls(new FakeVisibilityService()), new FakeJsonStore<List<PathEntry>>(), settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState(), []);
    }

    // Runs the window until it is idle: its jobs run, its layout done, and every read it queued
    // applied. The reads run on the session log's writer thread and come back to the UI thread through
    // the thread pool, so running the dispatcher's jobs alone would judge the window before a read that
    // is still on its way, and which one wins would depend on how warm and how busy the process is.
    private static void Settle(Window window, RecordsViewModel records)
    {
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var reads = records.Reads;
            if (reads.IsCompleted)
                return;
            RunUntil(reads);
        }
    }

    // Runs the window until the list shows what holds, a page at a time.
    private static void Until(Window window, RecordsViewModel records, Func<bool> holds)
    {
        Settle(window, records);
        while (!holds())
        {
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Applied() => applied.TrySetResult();
            records.PageApplied += Applied;
            try
            {
                if (!holds())
                    RunUntil(applied.Task.WaitAsync(TestContext.Current.CancellationToken));
            }
            finally
            {
                records.PageApplied -= Applied;
            }
            Settle(window, records);
        }
    }

    // Logs a record and waits until the database has stored it and the window has heard of it.
    private static void Arrive(Window window, RecordsViewModel records, string message)
    {
        using var stored = new ManualResetEventSlim();
        void Heard() => stored.Set();
        Log.RecordStored += Heard;
        try
        {
            Log.Info(message);
            stored.Wait(TestContext.Current.CancellationToken);
        }
        finally
        {
            Log.RecordStored -= Heard;
        }
        Settle(window, records);
    }

    // The first row whose container shows inside the viewport, and how far its top sits from the viewport's.
    private static (long Id, double Top) FirstVisible(ListBox list, ScrollViewer scroll) =>
        list.GetRealizedContainers()
            .Select(container => (container, top: container.TranslatePoint(default, scroll)!.Value.Y))
            .Where(item => item.top + item.container.Bounds.Height > 0)
            .OrderBy(item => item.top)
            .Select(item => (((RecordRowViewModel)item.container.DataContext!).Id, item.top))
            .First();

    private static double Remaining(ScrollViewer scroll) =>
        scroll.Extent.Height - scroll.Offset.Y - scroll.Viewport.Height;

    [AvaloniaFact]
    public void The_list_pages_through_every_record_and_new_ones_appear_only_at_its_top()
    {
        var output = TestContext.Current.TestOutputHelper;
        var store = RecordsStore.TryOpen(Path.Combine(_root, RecordsStore.FileName), out var failure) ?? throw failure!;
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < Seeded; index++)
            store.Write(new LogEntry(SeedSession, FileTimestamp.SerializedStamp(start.AddSeconds(index)), "info", $"seeded {index}", null, null));
        Log.Start(store, Path.Combine(_root, "logs"));
        try
        {
            var records = new RecordsViewModel(Log.CreateRecordsReader(), Log.Session, _delays.Delay);
            var window = Show(new RecordsWindow(records, Owner()));
            var list = window.FindControl<ListBox>("RecordList")!;
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            Until(window, records, () => records.ListStatus == RecordsListStatus.Ready && !records.IsLoadingMore);

            // (a) The first page: the 100 newest, newest first, filling more than the window.
            Assert.Equal(RecordsReader.PageSize, records.Rows.Count);
            Assert.Equal(Enumerable.Range(Seeded - RecordsReader.PageSize, RecordsReader.PageSize).Reverse().Select(i => $"seeded {i}"),
                records.Rows.Select(row => row.Record.Message));
            Assert.True(Remaining(scroll) > scroll.Viewport.Height);

            // (c) At the top, a new record appears at the top.
            Arrive(window, records, "live 1");
            RunUntil(_delays.Started.WaitAsync(TestContext.Current.CancellationToken));
            _delays.Elapse(RecordsViewModel.LiveInterval);
            Until(window, records, () => records.Rows[0].Record.Message == "live 1");
            Assert.True(scroll.Offset.Y < 1);

            // (d) Scrolled down, with a record selected: arrivals move nothing, then join at the top.
            scroll.Offset = new Vector(0, scroll.Viewport.Height * 2);
            Until(window, records, () => !records.IsLoadingMore);
            var selected = records.Rows[30];
            records.SelectedRow = selected;
            Settle(window, records);
            Assert.True(records.HasDetail);
            var offset = scroll.Offset;
            var first = FirstVisible(list, scroll);
            for (var arrival = 2; arrival <= 4; arrival++)
            {
                Arrive(window, records, $"live {arrival}");
                _delays.Elapse(RecordsViewModel.LiveInterval);
                Settle(window, records);
                Assert.Equal(offset, scroll.Offset);
                Assert.Equal(first, FirstVisible(list, scroll));
                Assert.Equal("live 1", records.Rows[0].Record.Message);
                Assert.Same(selected, records.SelectedRow);
                Assert.Equal(selected.Id, records.Detail!.Record.Id);
            }
            scroll.Offset = default;
            Until(window, records, () => records.Rows[0].Record.Message == "live 4");
            Assert.Equal(["live 4", "live 3", "live 2", "live 1"], records.Rows.Take(4).Select(row => row.Record.Message));
            Assert.Same(selected, records.SelectedRow);

            // (b) Just over a screen from the end reads nothing; within one screen reads the next page,
            // through every record. "Just over" is half a row: the list estimates the height of rows it
            // has not laid out, and realizing those at the new offset corrects the extent by a few
            // pixels, enough to carry a one-pixel margin inside the screen.
            var before = records.Rows.Count;
            var row = list.GetRealizedContainers().First().Bounds.Height;
            scroll.Offset = new Vector(0, scroll.Extent.Height - 2 * scroll.Viewport.Height - row / 2);
            Settle(window, records);
            Assert.Equal(before, records.Rows.Count);
            var slowest = TimeSpan.Zero;
            while (records.HasMore)
            {
                var shown = records.Rows.Count;
                var step = Stopwatch.StartNew();
                scroll.Offset = new Vector(0, scroll.Extent.Height - 2 * scroll.Viewport.Height + 1);
                Until(window, records, () => !records.HasMore || records.Rows.Count > shown && !records.IsLoadingMore);
                if (step.Elapsed > slowest)
                    slowest = step.Elapsed;
                // (e) Each page joins in one step; the window answers between them.
                Assert.Equal(Math.Min(shown + RecordsReader.PageSize, Seeded + 4), records.Rows.Count);
            }
            output?.WriteLine($"{Seeded} seeded records paged; slowest page step {slowest.TotalMilliseconds:0} ms");

            window.Close();
            Dispatcher.UIThread.RunJobs();
            Log.Shutdown();

            using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, RecordsStore.FileName)};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM log_entries ORDER BY time DESC, id DESC";
            var stored = new List<long>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    stored.Add(reader.GetInt64(0));
            }
            Assert.Equal(stored, records.Rows.Select(row => row.Id));
        }
        finally
        {
            Log.Shutdown();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }
}
