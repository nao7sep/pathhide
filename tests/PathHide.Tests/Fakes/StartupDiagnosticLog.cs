using System;
using System.IO;
using System.Threading.Tasks;
using PathHide.Services;

namespace PathHide.Tests.Fakes;

internal sealed class StartupDiagnosticLog : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pathhide-startup-tests", NanoId.New());
    internal TaskCompletionSource<LogEntry> Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal StartupDiagnosticLog(string expectedMessage) => Log.Start(new Sink(expectedMessage, Reported), _root);

    public void Dispose()
    {
        try { Log.Shutdown(); }
        finally
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Sink(string expectedMessage, TaskCompletionSource<LogEntry> reported) : ILogSink
    {
        public void Write(LogEntry entry)
        {
            if (entry.Message == expectedMessage)
                reported.TrySetResult(entry);
        }
        public void Dispose() { }
    }
}
