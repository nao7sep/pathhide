using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using PathHide.Models;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

public sealed class SessionLoggerTests
{
    private static readonly IReadOnlySet<string> Denied =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "token", "password", "secret" };

    private static SessionLogger NewLogger(StringWriter sw, bool debug = true) =>
        new(sw, debug, Denied, leaveOpen: true);

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
    public void Info_writes_the_time_level_message_envelope()
    {
        var sw = new StringWriter();
        NewLogger(sw).Info("hello");

        var line = Assert.Single(Lines(sw));
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
    public void Free_fields_are_merged_at_the_top_level()
    {
        var sw = new StringWriter();
        NewLogger(sw).Info("op", new { path = "/tmp/x", count = 3 });

        var line = Assert.Single(Lines(sw));
        Assert.Equal("/tmp/x", line["path"]!.GetValue<string>());
        Assert.Equal(3, line["count"]!.GetValue<int>());
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
    public void Denied_field_values_are_redacted()
    {
        var sw = new StringWriter();
        NewLogger(sw).Info("login", new { user = "bob", password = "hunter2" });

        var line = Assert.Single(Lines(sw));
        Assert.Equal("bob", line["user"]!.GetValue<string>());
        Assert.Equal(LogRedactor.Marker, line["password"]!.GetValue<string>());
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
        Assert.Equal("Hidden", line["state"]!.GetValue<string>());
    }

    [Fact]
    public void A_free_field_cannot_shadow_a_reserved_envelope_key()
    {
        var sw = new StringWriter();
        NewLogger(sw).Info("real", new { message = "fake", level = "fake" });

        var line = Assert.Single(Lines(sw));
        Assert.Equal("real", line["message"]!.GetValue<string>());
        Assert.Equal("info", line["level"]!.GetValue<string>());
    }

    [Fact]
    public void A_newline_in_a_value_stays_one_physical_line()
    {
        var sw = new StringWriter();
        NewLogger(sw).Info("note", new { body = "line1\nline2" });

        var line = Assert.Single(Lines(sw)); // would be 2+ if the newline were literal
        Assert.Equal("line1\nline2", line["body"]!.GetValue<string>());
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
        Assert.NotNull(line["logError"]);
    }

    [Fact]
    public void A_non_object_field_argument_is_ignored_not_crashed()
    {
        var sw = new StringWriter();
        NewLogger(sw).Info("m", "i am a bare string, not named fields");

        var line = Assert.Single(Lines(sw));
        Assert.Equal("m", line["message"]!.GetValue<string>());
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
    public void Flush_failure_is_reported_to_console_without_throwing()
    {
        var writer = new ThrowingFlushWriter();
        var log = new SessionLogger(writer, debugEnabled: true, Denied, leaveOpen: true);
        var originalErr = Console.Error;
        var console = new StringWriter();
        Console.SetError(console);

        try
        {
            var thrown = Record.Exception(() => log.Flush());

            Assert.Null(thrown);
            Assert.Contains("[logger] flush failed: IOException: disk full", console.ToString());
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public void Dispose_final_flush_failure_is_reported_to_console_without_throwing()
    {
        var writer = new ThrowingFlushWriter();
        var log = new SessionLogger(writer, debugEnabled: true, Denied, leaveOpen: true);
        var originalErr = Console.Error;
        var console = new StringWriter();
        Console.SetError(console);

        try
        {
            var thrown = Record.Exception(log.Dispose);

            Assert.Null(thrown);
            Assert.Contains("[logger] final flush failed: IOException: disk full", console.ToString());
        }
        finally
        {
            Console.SetError(originalErr);
        }
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

    private sealed class ThrowingFlushWriter : StringWriter
    {
        public override void Flush() =>
            throw new IOException("disk full");
    }
}
