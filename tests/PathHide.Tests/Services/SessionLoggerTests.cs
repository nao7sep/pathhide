using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Models;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

public sealed class SessionLoggerTests
{
    private static readonly DateTimeOffset SessionStart = new(2026, 10, 2, 9, 30, 15, 123, TimeSpan.Zero);

    private static SessionLogger NewLogger(StringWriter sw, bool debug = true) =>
        new(SessionStart, new TextWriterLogSink(sw, leaveOpen: true), fallbackDirectory: null, debug,
            writeInBackground: false);

    private static List<JsonNode> Lines(StringWriter sw)
    {
        var result = new List<JsonNode>();
        foreach (var raw in sw.ToString().Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0)
                result.Add(JsonNode.Parse(line)!);
        }
        return result;
    }

    [Fact]
    public void Info_writes_the_session_time_level_message_envelope()
    {
        var sw = new StringWriter();
        NewLogger(sw).Info("hello");

        var line = Assert.Single(Lines(sw));
        Assert.Equal("2026-10-02T09:30:15.123Z", line["session"]!.GetValue<string>());
        Assert.Equal("info", line["level"]!.GetValue<string>());
        Assert.Equal("hello", line["message"]!.GetValue<string>());
        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$",
            line["time"]!.GetValue<string>());
    }

    [Fact]
    public void Each_level_serializes_its_own_name()
    {
        var sw = new StringWriter();
        var log = NewLogger(sw);
        log.Debug("d");
        log.Info("i");
        log.Warn("w");
        log.Error("e");

        var lines = Lines(sw);
        Assert.Equal(new[] { "debug", "info", "warn", "error" },
            lines.ConvertAll(l => l!["level"]!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void Free_fields_are_kept_as_given()
    {
        var sw = new StringWriter();
        NewLogger(sw).Info("op", new { path = "/tmp/x", count = 3 });

        var fields = Assert.Single(Lines(sw))["fields"]!;
        Assert.Equal("/tmp/x", fields["path"]!.GetValue<string>());
        Assert.Equal(3, fields["count"]!.GetValue<int>());
    }

    [Fact]
    public void Each_event_is_one_object_on_one_line()
    {
        var sw = new StringWriter();
        var log = NewLogger(sw);
        log.Info("a");
        log.Info("b");

        Assert.Equal(2, Lines(sw).Count);
    }

    [Fact]
    public void Debug_is_dropped_when_debug_is_disabled()
    {
        var sw = new StringWriter();
        NewLogger(sw, debug: false).Debug("nope");

        Assert.Empty(Lines(sw));
    }

    [Fact]
    public void Debug_is_written_when_debug_is_enabled()
    {
        var sw = new StringWriter();
        NewLogger(sw, debug: true).Debug("yep");

        var line = Assert.Single(Lines(sw));
        Assert.Equal("debug", line["level"]!.GetValue<string>());
    }

    [Fact]
    public void Exceptions_are_captured_with_type_message_and_cause_chain()
    {
        var sw = new StringWriter();
        var ex = new InvalidOperationException("outer", new ArgumentException("inner"));
        NewLogger(sw).Error("boom", ex);

        var error = Assert.Single(Lines(sw))["error"]!;
        Assert.Contains("InvalidOperationException", error["type"]!.GetValue<string>());
        Assert.Equal("outer", error["message"]!.GetValue<string>());
        Assert.Equal("inner", error["cause"]!["message"]!.GetValue<string>());
        Assert.Contains("ArgumentException", error["cause"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void AggregateException_captures_every_cause()
    {
        var sw = new StringWriter();
        var ex = new AggregateException(new Exception("one"), new Exception("two"));
        NewLogger(sw).Error("batch failed", ex);

        var causes = (JsonArray)Assert.Single(Lines(sw))["error"]!["causes"]!;
        Assert.Equal(2, causes.Count);
        Assert.Equal("one", causes[0]!["message"]!.GetValue<string>());
        Assert.Equal("two", causes[1]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Enum_fields_are_written_by_name_not_number()
    {
        var sw = new StringWriter();
        NewLogger(sw).Info("scanned", new { state = ActualState.Hidden });

        var line = Assert.Single(Lines(sw));
        Assert.Equal("Hidden", line["fields"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void A_free_field_named_like_the_envelope_is_kept_beside_it()
    {
        var sw = new StringWriter();
        NewLogger(sw).Info("real", new { message = "fake", level = "fake" });

        var line = Assert.Single(Lines(sw));
        Assert.Equal("real", line["message"]!.GetValue<string>());
        Assert.Equal("info", line["level"]!.GetValue<string>());
        Assert.Equal("fake", line["fields"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void A_newline_in_a_value_stays_one_physical_line()
    {
        var sw = new StringWriter();
        NewLogger(sw).Info("note", new { body = "line1\nline2" });

        var line = Assert.Single(Lines(sw)); // would be 2+ if the newline were literal
        Assert.Equal("line1\nline2", line["fields"]!["body"]!.GetValue<string>());
    }

    [Fact]
    public void A_field_that_cannot_serialize_falls_back_without_throwing()
    {
        var sw = new StringWriter();

        // double.NaN is not valid JSON, so serialization throws; the logger must
        // still emit a usable line rather than propagate the failure.
        var thrown = Record.Exception(() => NewLogger(sw).Info("bad", new { value = double.NaN }));

        Assert.Null(thrown);
        var line = Assert.Single(Lines(sw));
        Assert.Equal("bad", line["message"]!.GetValue<string>());
        Assert.Equal("ArgumentException", line["fields"]!["logError"]!.GetValue<string>());
    }

    [Fact]
    public void A_non_object_field_argument_is_ignored_not_crashed()
    {
        var sw = new StringWriter();
        NewLogger(sw).Info("m", "i am a bare string, not named fields");

        var line = Assert.Single(Lines(sw));
        Assert.Equal("m", line["message"]!.GetValue<string>());
        Assert.Null(line["fields"]);
    }

    [Fact]
    public void Dispose_with_leaveOpen_does_not_close_the_shared_writer()
    {
        var sw = new StringWriter();
        var log = NewLogger(sw); // leaveOpen: true
        log.Info("before");
        log.Dispose();

        // The shared writer survives the logger, as the console must at shutdown.
        var afterDispose = Record.Exception(() => sw.Write("still open"));
        Assert.Null(afterDispose);
    }

    [Fact]
    public void Writing_after_dispose_falls_back_to_console_not_a_silent_drop()
    {
        // A worker-thread log can race shutdown's Dispose. The event must surface
        // somewhere (the console) rather than vanish — never silently swallowed.
        var sw = new StringWriter();
        var log = NewLogger(sw);
        log.Dispose();

        var originalErr = Console.Error;
        var console = new StringWriter();
        Console.SetError(console);
        try
        {
            var thrown = Record.Exception(() => log.Info("late", new { path = "/x" }));
            Assert.Null(thrown);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        // Nothing reached the disposed file writer...
        Assert.Empty(Lines(sw));
        // ...but the event surfaced on the console instead of being dropped.
        Assert.Contains("late", console.ToString());
        Assert.Contains("/x", console.ToString());
    }

    [Fact]
    public void Import_writes_another_sessions_entries_as_given()
    {
        var sw = new StringWriter();
        var child = new LogEntry("2026-10-02T09:31:00.000Z", "2026-10-02T09:31:00.500Z", "error", "apply: failed",
            new JsonObject { ["path"] = @"C:\x" }, null);

        NewLogger(sw).Import([child]);

        var line = Assert.Single(Lines(sw));
        Assert.Equal("2026-10-02T09:31:00.000Z", line["session"]!.GetValue<string>());
        Assert.Equal("2026-10-02T09:31:00.500Z", line["time"]!.GetValue<string>());
        Assert.Equal(@"C:\x", line["fields"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void Without_a_sink_entries_go_to_the_sessions_fallback_file()
    {
        using var temp = new TempDirectory();
        var log = new SessionLogger(SessionStart, sink: null, temp.Path, debugEnabled: true, writeInBackground: false);

        log.Info("one");
        log.Info("two");

        var lines = File.ReadAllLines(Path.Combine(temp.Path, "20261002-093015-123-utc.log"));
        Assert.Equal(["one", "two"], Array.ConvertAll(lines, l => JsonNode.Parse(l)!["message"]!.GetValue<string>()));
    }

    [Fact]
    public void An_entry_the_sink_refuses_goes_to_the_fallback_file_with_the_reason()
    {
        using var temp = new TempDirectory();
        var log = new SessionLogger(SessionStart, new ThrowingSink(), temp.Path, debugEnabled: true, writeInBackground: false);

        var thrown = Record.Exception(() => log.Info("kept"));

        Assert.Null(thrown);
        var lines = File.ReadAllLines(Path.Combine(temp.Path, "20261002-093015-123-utc.log"));
        Assert.Equal(2, lines.Length);
        Assert.Equal("kept", JsonNode.Parse(lines[0])!["message"]!.GetValue<string>());
        var reason = JsonNode.Parse(lines[1])!;
        Assert.Equal("error", reason["level"]!.GetValue<string>());
        Assert.Equal("database is locked", reason["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void When_the_fallback_file_fails_too_the_entry_reaches_the_console()
    {
        using var temp = new TempDirectory();
        var blocked = Path.Combine(temp.Path, "not-a-folder");
        File.WriteAllText(blocked, "");
        var log = new SessionLogger(SessionStart, new ThrowingSink(), blocked, debugEnabled: true, writeInBackground: false);
        var originalErr = Console.Error;
        var console = new StringWriter();
        Console.SetError(console);

        try
        {
            var thrown = Record.Exception(() => log.Warn("last resort"));
            Assert.Null(thrown);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        Assert.Contains("last resort", console.ToString());
        Assert.Contains("[logger] sink write failed: IOException: database is locked", console.ToString());
    }

    [Fact]
    public async Task A_background_logger_returns_while_its_sink_is_still_writing()
    {
        var sink = new BlockingSink();
        var log = new SessionLogger(SessionStart, sink, fallbackDirectory: null, debugEnabled: true, writeInBackground: true);

        Task? second = null;
        var returned = false;
        try
        {
            log.Info("first");
            Assert.True(sink.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            second = Task.Run(() => log.Info("second"), TestContext.Current.CancellationToken);
            returned = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)) == second;
        }
        finally
        {
            sink.Release.Set();
            if (second is not null)
                await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            log.Dispose();
        }

        Assert.True(returned);
        Assert.Equal(["first", "second"], sink.Messages);
        Assert.True(sink.Disposed);
    }

    [Fact]
    public void Closing_a_background_logger_writes_every_queued_entry_in_order()
    {
        var sink = new BlockingSink();
        sink.Release.Set();
        var log = new SessionLogger(SessionStart, sink, fallbackDirectory: null, debugEnabled: true, writeInBackground: true);

        for (var i = 0; i < 50; i++)
            log.Info("entry " + i);
        log.Dispose();

        Assert.Equal(Enumerable.Range(0, 50).Select(i => "entry " + i), sink.Messages);
        Assert.True(sink.Disposed);
    }

    [Fact]
    public void Closing_past_the_bound_sends_the_queued_entries_to_the_fallback_file()
    {
        using var temp = new TempDirectory();
        var sink = new BlockingSink();
        var log = new SessionLogger(SessionStart, sink, temp.Path, debugEnabled: true, writeInBackground: true);

        try
        {
            log.Info("stuck");
            Assert.True(sink.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            log.Info("queued");
            log.Close(TimeSpan.FromMilliseconds(50));
            var path = Path.Combine(temp.Path, "20261002-093015-123-utc.log");
            Assert.True(SpinWait.SpinUntil(() => File.Exists(path) && File.ReadAllLines(path).Length == 2,
                TimeSpan.FromSeconds(5)));
            var lines = File.ReadAllLines(path);
            Assert.Equal("queued", JsonNode.Parse(lines[0])!["message"]!.GetValue<string>());
            var note = JsonNode.Parse(lines[1])!;
            Assert.Equal("error", note["level"]!.GetValue<string>());
            Assert.Equal(1, note["fields"]!["left"]!.GetValue<int>());
            Assert.False(sink.Disposed);
        }
        finally
        {
            sink.Release.Set();
            Assert.True(SpinWait.SpinUntil(() => sink.Disposed, TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void An_entry_after_a_background_logger_closes_goes_to_the_fallback_file()
    {
        using var temp = new TempDirectory();
        var sink = new BlockingSink();
        sink.Release.Set();
        var log = new SessionLogger(SessionStart, sink, temp.Path, debugEnabled: true, writeInBackground: true);
        log.Dispose();

        log.Info("late");

        Assert.Empty(sink.Messages);
        var line = Assert.Single(File.ReadAllLines(Path.Combine(temp.Path, "20261002-093015-123-utc.log")));
        Assert.Equal("late", JsonNode.Parse(line)!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Each_entry_the_sink_stores_signals_once_it_is_stored()
    {
        var sw = new StringWriter();
        var stored = new List<int>();
        var log = new SessionLogger(SessionStart, new TextWriterLogSink(sw, leaveOpen: true), fallbackDirectory: null,
            debugEnabled: true, writeInBackground: false, stored: () => stored.Add(Lines(sw).Count));

        log.Info("a");
        log.Warn("b");

        // Each signal came after its own line was written, never before it.
        Assert.Equal([1, 2], stored);
    }

    [Fact]
    public void An_entry_that_went_to_the_fallback_file_signals_nothing()
    {
        using var temp = new TempDirectory();
        var signals = 0;
        var log = new SessionLogger(SessionStart, new ThrowingSink(), temp.Path, debugEnabled: true,
            writeInBackground: false, stored: () => signals++);

        log.Info("not stored");

        Assert.Equal(0, signals);
        Assert.NotEmpty(File.ReadAllLines(Path.Combine(temp.Path, "20261002-093015-123-utc.log")));
    }

    [Fact]
    public void A_listener_that_throws_loses_no_entry_and_stops_nothing()
    {
        var sw = new StringWriter();
        var log = new SessionLogger(SessionStart, new TextWriterLogSink(sw, leaveOpen: true), fallbackDirectory: null,
            debugEnabled: true, writeInBackground: true, stored: () => throw new InvalidOperationException("listener"));

        log.Info("a");
        log.Info("b");
        log.Dispose();

        Assert.Equal(["a", "b"], Lines(sw).ConvertAll(line => line["message"]!.GetValue<string>()));
    }

    [Fact]
    public void ClosingIncludesAStalledSinkDisposeWithinTheBound()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var settled = new ManualResetEventSlim();
        var logger = new SessionLogger(SessionStart, new BlockingCloseSink(entered, release, settled),
            null, true, writeInBackground: true);
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            logger.Close(TimeSpan.FromMilliseconds(50));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.False(settled.IsSet);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.Set();
            Assert.True(settled.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
    }

    private sealed class BlockingCloseSink(ManualResetEventSlim entered, ManualResetEventSlim release,
        ManualResetEventSlim settled) : ILogSink
    {
        public void Write(LogEntry entry) { }
        public void Dispose()
        {
            entered.Set();
            release.Wait();
            settled.Set();
        }
    }

    private sealed class BlockingSink : ILogSink
    {
        private readonly List<string> _messages = [];

        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public bool Disposed { get; private set; }

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
            Entered.Set();
            Release.Wait();
            lock (_messages)
                _messages.Add(entry.Message);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class ThrowingSink : ILogSink
    {
        public void Write(LogEntry entry) => throw new IOException("database is locked");

        public void Dispose()
        {
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("pathhide-tests-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
