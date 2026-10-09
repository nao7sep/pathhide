using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PathHide.Services;
using PathHide.Storage;
using Xunit;

namespace PathHide.Tests.Storage;

/// <summary>The records window's reads, against a real records database written by <see cref="RecordsStore"/>.</summary>
public sealed class RecordsReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pathhide-records-reader-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordsStore _store;
    private readonly RecordsReader _reader;

    public RecordsReaderTests()
    {
        var path = Path.Combine(_root, RecordsStore.FileName);
        _store = RecordsStore.TryOpen(path, out var failure) ?? throw failure!;
        _reader = new RecordsReader(path);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
    }

    private static string At(int second) =>
        FileTimestamp.SerializedStamp(new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero).AddSeconds(second));

    private void Write(int second, string message, string level = "info", string session = "s1",
        JsonObject? fields = null, JsonObject? error = null) =>
        _store.Write(new LogEntry(session, At(second), level, message, fields, error));

    private static RecordsQuery All(string search = "") => new(null, null, search, null);

    [Fact]
    public async Task A_page_lists_the_newest_first()
    {
        Write(1, "first");
        Write(3, "third");
        Write(2, "second");

        var page = await _reader.ReadPageAsync(All());

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
            var page = await _reader.ReadPageAsync(All() with { After = after });
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

        var page = await _reader.ReadPageAsync(All() with { Session = "2026-10-03T00:00:00.000Z" });

        Assert.Equal("old launch", Assert.Single(page.Records).Message);
    }

    [Fact]
    public async Task Warnings_and_errors_is_every_warning_and_error_and_a_level_is_just_that_level()
    {
        Write(1, "e", "error");
        Write(2, "w", "warn");
        Write(3, "i", "info");
        Write(4, "d", "debug");

        var warningsAndErrors = await _reader.ReadPageAsync(All() with { Level = RecordLevelFilter.WarningsAndErrors });
        var debug = await _reader.ReadPageAsync(All() with { Level = RecordLevelFilter.Debug });

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

        Assert.Equal("apply: start", Assert.Single((await _reader.ReadPageAsync(All("APPLY"))).Records).Message);
        Assert.Equal("scan", Assert.Single((await _reader.ReadPageAsync(All("secret folder"))).Records).Message);
        Assert.Equal("save failed", Assert.Single((await _reader.ReadPageAsync(All("  disk full  "))).Records).Message);
        Assert.Equal("100% done", Assert.Single((await _reader.ReadPageAsync(All("0%"))).Records).Message);
        Assert.Empty((await _reader.ReadPageAsync(All("1_0"))).Records);
        Assert.Equal(5, (await _reader.ReadPageAsync(All("   "))).Records.Count);
    }

    [Fact]
    public async Task A_record_reads_whole_as_stored()
    {
        Write(1, "save failed", "error", "s9",
            new JsonObject { ["label"] = "paths" }, new JsonObject { ["type"] = "System.IO.IOException" });
        var id = Assert.Single((await _reader.ReadPageAsync(All())).Records).Id;

        var record = await _reader.ReadDetailAsync(id);

        Assert.NotNull(record);
        Assert.Equal(("s9", At(1), "error", "save failed"), (record.Session, record.Time, record.Level, record.Message));
        Assert.Equal("{\"label\":\"paths\"}", record.Fields);
        Assert.Equal("{\"type\":\"System.IO.IOException\"}", record.Error);
        Assert.Null(await _reader.ReadDetailAsync(id + 1));
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
            await _reader.ReadSessionsAsync());
    }

    [Fact]
    public async Task Reading_writes_nothing_to_the_database()
    {
        Write(1, "only");
        var id = Assert.Single((await _reader.ReadPageAsync(All())).Records).Id;

        await _reader.ReadPageAsync(All("only"));
        await _reader.ReadDetailAsync(id);
        await _reader.ReadSessionsAsync();

        Assert.Single((await _reader.ReadPageAsync(All())).Records);
    }

    [Fact]
    public async Task A_database_that_is_not_there_fails_the_read()
    {
        var missing = new RecordsReader(Path.Combine(_root, "missing", RecordsStore.FileName));

        await Assert.ThrowsAsync<SqliteException>(() => missing.ReadPageAsync(All()));
        Assert.False(File.Exists(Path.Combine(_root, "missing", RecordsStore.FileName)));
    }

    [Fact]
    public void A_search_pattern_escapes_its_own_wildcards()
    {
        Assert.Null(RecordsReader.LikePattern("  "));
        Assert.Equal(@"%a\%b\_c\\d%", RecordsReader.LikePattern(@" a%b_c\d "));
    }
}
