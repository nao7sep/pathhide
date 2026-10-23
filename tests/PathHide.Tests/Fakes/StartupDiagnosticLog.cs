using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PathHide.Services;

namespace PathHide.Tests.Fakes;

internal sealed class StartupDiagnosticLog : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pathhide-startup-tests", Guid.NewGuid().ToString("N"));
    private readonly Sink _sink = new();

    internal TaskCompletionSource<LogEntry> Reported { get; }

    internal StartupDiagnosticLog(string expectedMessage)
    {
        Reported = _sink.Await(entry => entry.Message == expectedMessage);
        Log.Start(_sink, _root);
    }

    /// <summary>The first record, already written or still to come, that <paramref name="matches"/> accepts.</summary>
    internal Task<LogEntry> Find(Func<LogEntry, bool> matches) => _sink.Await(matches).Task;

    public void Dispose()
    {
        try { Log.Shutdown(); }
        finally
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Sink : ILogSink
    {
        private readonly List<LogEntry> _written = [];
        private readonly List<(Func<LogEntry, bool> Matches, TaskCompletionSource<LogEntry> Found)> _waiting = [];

        public TaskCompletionSource<LogEntry> Await(Func<LogEntry, bool> matches)
        {
            var found = new TaskCompletionSource<LogEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_written)
            {
                var earlier = _written.Find(entry => matches(entry));
                if (earlier is not null)
                    found.SetResult(earlier);
                else
                    _waiting.Add((matches, found));
            }
            return found;
        }

        public void Write(LogEntry entry)
        {
            lock (_written)
            {
                _written.Add(entry);
                _waiting.RemoveAll(waiter => waiter.Matches(entry) && waiter.Found.TrySetResult(entry));
            }
        }

        public void Dispose() { }
    }
}
