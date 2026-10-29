using System;
using System.IO;
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
    // The shutdown record says why the launch ended: a session the operating system ends is not a quit.
    [Fact]
    public void The_exit_reason_tells_a_quit_from_a_session_end_and_a_fatal_failure()
    {
        var logs = Directory.CreateTempSubdirectory("pathhide-log-reason-").FullName;
        try
        {
            Log.Start(null, logs);
            Assert.Equal("quit", Log.ExitReason(clean: true));
            Assert.Equal("fatal", Log.ExitReason(clean: false));
            Log.EndingSession();
            Assert.Equal("session end", Log.ExitReason(clean: true));
            Log.Shutdown();

            Log.Start(null, logs);
            Assert.Equal("quit", Log.ExitReason(clean: true));
        }
        finally
        {
            Log.Shutdown();
            Directory.Delete(logs, recursive: true);
        }
    }

    // The drain waits for the queue until a quit hands it what remains of the exit deadline, and then
    // never past it, a session end included.
    [Fact]
    public void The_drain_draws_on_what_remains_of_the_exit_deadline()
    {
        var logs = Directory.CreateTempSubdirectory("pathhide-log-drain-").FullName;
        try
        {
            Log.Start(null, logs);
            Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, Log.DrainBound);
            Log.EndingSession();
            Log.DrainWithin(TimeSpan.FromSeconds(2));
            Assert.InRange(Log.DrainBound, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            Log.DrainWithin(TimeSpan.Zero);
            Assert.Equal(TimeSpan.Zero, Log.DrainBound);
        }
        finally
        {
            Log.Shutdown();
            Directory.Delete(logs, recursive: true);
        }
    }
}
