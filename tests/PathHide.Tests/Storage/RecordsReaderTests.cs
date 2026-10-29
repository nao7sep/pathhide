using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PathHide.Services;
using PathHide.Storage;
using PathHide.Tests.Fakes;
using Xunit;

namespace PathHide.Tests.Storage;

/// <summary>
/// The records window's reads, against a real records database written by <see cref="RecordsStore"/>,
/// through the session logger that owns its connection, as in the app.
/// </summary>
[Collection(ProcessLogCollection.Name)]
public sealed class RecordsReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pathhide-records-reader-tests", Guid.NewGuid().ToString("N"));
    private readonly SessionLogger _owner;
    private readonly RecordsReader _reader;

    public RecordsReaderTests()
    {
        var store = RecordsStore.TryOpen(Path.Combine(_root, RecordsStore.FileName), out var failure) ?? throw failure!;
        _owner = new SessionLogger(DateTimeOffset.UtcNow, store, null, debugEnabled: false, writeInBackground: true);
        _reader = new RecordsReader(_owner);
    }

    public void Dispose()
    {
        _owner.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
    }

    private static string At(int second) =>
        FileTimestamp.SerializedStamp(new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero).AddSeconds(second));

    private void Write(int second, string message, string level = "info", string session = "s1",
        JsonObject? fields = null, JsonObject? error = null) =>
        _owner.Import([new LogEntry(session, At(second), level, message, fields, error)]);

    private static RecordsQuery All(string search = "") => new(null, null, search, null);

    [Fact]
    public async Task Incremental_reads_use_insertion_identity_and_a_fixed_upper_bound()
    {
        var token = TestContext.Current.CancellationToken;
        Write(50, "initial");
        var initial = await _reader.ReadPageAsync(All(), token);
        for (var i = 0; i < RecordsReader.PageSize + 1; i++) Write(1, "late");
        var first = await _reader.ReadPageAsync(All() with { InsertedAfter = initial.UpperId }, token);
        Assert.True(first.More);
        Assert.All(first.Records, record => Assert.Equal("late", record.Message));
        Write(60, "next signal");
        var rest = await _reader.ReadPageAsync(All() with { InsertedAfter = first.Records[^1].Id, ThroughId = first.UpperId }, token);
        Assert.False(rest.More);
        Assert.Equal("late", Assert.Single(rest.Records).Message);
        var filtered = await _reader.ReadPageAsync(All("absent") with { InsertedAfter = rest.UpperId }, token);
        Assert.Empty(filtered.Records);
        Assert.True(filtered.UpperId > rest.UpperId);
        var next = await _reader.ReadPageAsync(All() with { InsertedAfter = rest.UpperId }, token);
        Assert.Equal("next signal", Assert.Single(next.Records).Message);
    }

    // A read its caller no longer needs ends as cancelled, not as a failure the window would report.
    [Fact]
    public async Task A_cancelled_read_ends_as_cancelled()
    {
        Write(1, "first");
        using var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadPageAsync(All("first"), cancelled.Token));
    }

    [Fact]
    public async Task A_page_lists_the_newest_first()
    {
        Write(1, "first");
        Write(3, "third");
        Write(2, "second");

        var page = await _reader.ReadPageAsync(All(), TestContext.Current.CancellationToken);

        Assert.Equal(["third", "second", "first"], page.Records.Select(record => record.Message));
        Assert.False(page.More);
    }

    [Fact]
    public async Task Pages_follow_their_cursor_a_hundred_at_a_time_without_a_gap_or_a_repeat()
    {
        for (var second = 0; second < 230; second++)
            Write(second, $"m{second}");
        // Two records in one millisecond are still told apart, by their id.
        Write(100, "same time");

        var seen = new System.Collections.Generic.List<RecordSummary>();
        RecordCursor? after = null;
        var pages = 0;
        while (true)
        {
            var page = await _reader.ReadPageAsync(All() with { After = after }, TestContext.Current.CancellationToken);
            pages++;
            seen.AddRange(page.Records);
            if (!page.More)
                break;
            Assert.Equal(RecordsReader.PageSize, page.Records.Count);
            after = new RecordCursor(page.Records[^1].Time, page.Records[^1].Id);
        }

        Assert.Equal(3, pages);
        Assert.Equal(231, seen.Count);
        Assert.Equal(231, seen.Select(record => record.Id).Distinct().Count());
        Assert.Equal(seen.OrderByDescending(record => record.Time, StringComparer.Ordinal).ThenByDescending(record => record.Id), seen);
    }

    [Fact]
    public async Task A_page_keeps_to_one_launch()
    {
        Write(1, "old launch", session: "2026-10-03T00:00:00.000Z");
        Write(2, "this launch", session: "2026-10-04T00:00:00.000Z");

        var page = await _reader.ReadPageAsync(All() with { Session = "2026-10-03T00:00:00.000Z" }, TestContext.Current.CancellationToken);

        Assert.Equal("old launch", Assert.Single(page.Records).Message);
    }

    [Fact]
    public async Task Warnings_and_errors_is_every_warning_and_error_and_a_level_is_just_that_level()
    {
        Write(1, "e", "error");
        Write(2, "w", "warn");
        Write(3, "i", "info");
        Write(4, "d", "debug");

        var warningsAndErrors = await _reader.ReadPageAsync(All() with { Level = RecordLevelFilter.WarningsAndErrors }, TestContext.Current.CancellationToken);
        var debug = await _reader.ReadPageAsync(All() with { Level = RecordLevelFilter.Debug }, TestContext.Current.CancellationToken);

        Assert.Equal(["w", "e"], warningsAndErrors.Records.Select(record => record.Message));
        Assert.Equal("d", Assert.Single(debug.Records).Message);
    }

    [Fact]
    public async Task Search_finds_the_words_in_the_message_the_fields_or_the_error_and_takes_wildcards_literally()
    {
        Write(1, "apply: start");
        Write(2, "scan", fields: new JsonObject { ["path"] = "/Users/me/Secret Folder" });
        Write(3, "save failed", "error", error: new JsonObject { ["message"] = "disk full" });
        Write(4, "100% done");
        Write(5, "1000 done");

        Assert.Equal("apply: start", Assert.Single((await _reader.ReadPageAsync(All("APPLY"), TestContext.Current.CancellationToken)).Records).Message);
        Assert.Equal("scan", Assert.Single((await _reader.ReadPageAsync(All("secret folder"), TestContext.Current.CancellationToken)).Records).Message);
        Assert.Equal("save failed", Assert.Single((await _reader.ReadPageAsync(All("  disk full  "), TestContext.Current.CancellationToken)).Records).Message);
        Assert.Equal("100% done", Assert.Single((await _reader.ReadPageAsync(All("0%"), TestContext.Current.CancellationToken)).Records).Message);
        Assert.Empty((await _reader.ReadPageAsync(All("1_0"), TestContext.Current.CancellationToken)).Records);
        Assert.Equal(5, (await _reader.ReadPageAsync(All("   "), TestContext.Current.CancellationToken)).Records.Count);
    }

    [Fact]
    public async Task A_record_reads_whole_as_stored()
    {
        Write(1, "save failed", "error", "s9",
            new JsonObject { ["label"] = "paths" }, new JsonObject { ["type"] = "System.IO.IOException" });
        var id = Assert.Single((await _reader.ReadPageAsync(All(), TestContext.Current.CancellationToken)).Records).Id;

        var record = await _reader.ReadDetailAsync(id, TestContext.Current.CancellationToken);

        Assert.NotNull(record);
        Assert.Equal(("s9", At(1), "error", "save failed"), (record.Session, record.Time, record.Level, record.Message));
        Assert.Equal("{\"label\":\"paths\"}", record.Fields);
        Assert.Equal("{\"type\":\"System.IO.IOException\"}", record.Error);
        Assert.Null(await _reader.ReadDetailAsync(id + 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_launches_are_listed_newest_first()
    {
        Write(1, "a", session: "2026-10-02T00:00:00.000Z");
        Write(2, "b", session: "2026-10-04T00:00:00.000Z");
        Write(3, "c", session: "2026-10-03T00:00:00.000Z");
        Write(4, "d", session: "2026-10-04T00:00:00.000Z");

        Assert.Equal(
            ["2026-10-04T00:00:00.000Z", "2026-10-03T00:00:00.000Z", "2026-10-02T00:00:00.000Z"],
            await _reader.ReadSessionsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reading_writes_nothing_to_the_database()
    {
        Write(1, "only");
        var id = Assert.Single((await _reader.ReadPageAsync(All(), TestContext.Current.CancellationToken)).Records).Id;

        await _reader.ReadPageAsync(All("only"), TestContext.Current.CancellationToken);
        await _reader.ReadDetailAsync(id, TestContext.Current.CancellationToken);
        await _reader.ReadSessionsAsync(TestContext.Current.CancellationToken);

        Assert.Single((await _reader.ReadPageAsync(All(), TestContext.Current.CancellationToken)).Records);
    }

    // A session whose records database did not open logs to its fallback file and has nothing to read.
    [Fact]
    public async Task Without_a_records_database_the_read_fails()
    {
        using var owner = new SessionLogger(DateTimeOffset.UtcNow, null, null, debugEnabled: false, writeInBackground: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RecordsReader(owner).ReadPageAsync(All(), TestContext.Current.CancellationToken));
    }

    // The search matches what the detail pane shows, not the JSON escapes underneath it, without case
    // in any script, and takes wildcard characters as the text they are.
    [Fact]
    public async Task Search_matches_decoded_paths_quotes_and_case_in_any_script()
    {
        Write(1, "scan", fields: new JsonObject { ["path"] = @"C:\Users\me\Report ""final"".txt" });
        Write(2, "scan", fields: new JsonObject { ["path"] = "/Users/me/Документы/Äpfel" });
        Write(3, "scan", fields: new JsonObject { ["path"] = "/Users/me/100%_done" });
        Write(4, "save failed", "error", error: new JsonObject { ["type"] = "System.IO.IOException", ["message"] = @"D:\Data is full" });

        async Task<long[]> Found(string search) =>
            (await _reader.ReadPageAsync(All(search), TestContext.Current.CancellationToken)).Records.Select(record => record.Id).Order().ToArray();
        var ids = (await _reader.ReadPageAsync(All(), TestContext.Current.CancellationToken)).Records.Select(record => record.Id).Order().ToArray();

        Assert.Equal([ids[0]], await Found(@"c:\users\ME"));
        Assert.Equal([ids[0]], await Found(@"""final"""));
        Assert.Equal([ids[1]], await Found("документы/äpfel"));
        Assert.Equal([ids[2]], await Found("%_d"));
        Assert.Equal([ids[3]], await Found(@"d:\data"));
        Assert.Equal([ids[3]], await Found("ioexception"));
        Assert.Empty(await Found("\\\\"));
    }

    // Search matches exactly what the detail pane shows, so a value copied from it finds its record,
    // the words true, false and null included.
    [Fact]
    public async Task A_value_copied_from_the_detail_finds_its_record()
    {
        Write(1, "apply", fields: new JsonObject { ["path"] = @"C:\Users\me\a.txt", ["elevated"] = true, ["parent"] = null });
        Write(2, "scan", fields: new JsonObject { ["path"] = "/other" });
        var id = (await _reader.ReadPageAsync(All(), TestContext.Current.CancellationToken)).Records.Single(record => record.Message == "apply").Id;
        var detail = new PathHide.ViewModels.RecordDetailViewModel((await _reader.ReadDetailAsync(id, TestContext.Current.CancellationToken))!, "s1");

        foreach (var line in detail.FieldsText!.Split('\n'))
        {
            var value = line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..];
            Assert.Equal([id], (await _reader.ReadPageAsync(All(value), TestContext.Current.CancellationToken)).Records.Select(record => record.Id).ToArray());
        }
    }

    // A record whose details cannot be read is skipped, so one damaged row cannot fail every search. It
    // is reported once: the report is itself a record, which signals an open window to read again.
    [Fact]
    public async Task A_damaged_record_is_skipped_instead_of_failing_the_search_and_reported_once()
    {
        using var log = new StartupDiagnosticLog("records test: done");
        Write(1, "scan", fields: new JsonObject { ["path"] = "/findme" });
        Write(2, "scan", fields: new JsonObject { ["path"] = "/damaged" });
        await _reader.ReadSessionsAsync(TestContext.Current.CancellationToken); // the writes have landed
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(_root, RecordsStore.FileName) + ";Pooling=False"))
        {
            connection.Open();
            using var damage = connection.CreateCommand();
            damage.CommandText = "UPDATE log_entries SET fields = '{not json' WHERE fields LIKE '%damaged%'";
            Assert.Equal(1, damage.ExecuteNonQuery());
        }

        var found = await _reader.ReadPageAsync(All("findme"), TestContext.Current.CancellationToken);
        await _reader.ReadPageAsync(All("findme"), TestContext.Current.CancellationToken);
        Log.Info("records test: done");
        await log.Reported.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("scan", Assert.Single(found.Records).Message);
        Assert.Single(log.Written, entry => entry.Message == "records: search skipped records whose details cannot be read");
    }
}
