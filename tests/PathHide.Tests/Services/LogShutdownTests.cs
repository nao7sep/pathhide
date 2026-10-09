using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using PathHide.Services;
using PathHide.Tests.Storage;
using Xunit;

namespace PathHide.Tests.Services;

/// <summary>
/// The session log's drain at exit. These start and shut down the process-wide <see cref="Log"/>, so
/// they run in the serial storage collection, where nothing else logs beside them.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class LogShutdownTests
{
    [Theory]
    [InlineData(false, 5000)]
    [InlineData(true, 1000)]
    public void A_stalled_sink_gets_the_shorter_drain_when_the_os_ends_the_session(bool sessionEnding, long waitedMs)
    {
        var logs = Directory.CreateTempSubdirectory("pathhide-log-shutdown-").FullName;
        var sink = new StalledSink();
        try
        {
            Log.Start(sink, logs);
            Log.Info("stuck");
            Assert.True(sink.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Log.Info("queued");
            if (sessionEnding)
                Log.EndingSession();

            Log.Shutdown();

            // What the drain left goes to the fallback file, with a note of how long it waited.
            string[] lines = [];
            Assert.True(SpinWait.SpinUntil(() =>
            {
                var file = Directory.GetFiles(logs, "*.log").SingleOrDefault();
                lines = file is null ? [] : File.ReadAllLines(file);
                return lines.Length == 2;
            }, TimeSpan.FromSeconds(5)));
            Assert.Equal(waitedMs, JsonNode.Parse(lines[1])!["fields"]!["waitedMs"]!.GetValue<long>());
        }
        finally
        {
            sink.Release.Set();
            Assert.True(SpinWait.SpinUntil(() => sink.Disposed, TimeSpan.FromSeconds(5)));
            Directory.Delete(logs, recursive: true);
        }
    }

    private sealed class StalledSink : ILogSink
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public bool Disposed { get; private set; }

        public void Write(LogEntry entry)
        {
            Entered.Set();
            Release.Wait();
        }

        public void Dispose() => Disposed = true;
    }
}
