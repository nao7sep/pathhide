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

    [Fact]
    public void The_drain_is_shorter_when_the_os_ends_the_session()
    {
        var logs = Directory.CreateTempSubdirectory("pathhide-log-drain-").FullName;
        try
        {
            Log.Start(null, logs);
            Assert.Equal(SessionLogger.DrainBound, Log.DrainBound);
            Log.EndingSession();
            Assert.Equal(SessionLogger.SessionEndDrainBound, Log.DrainBound);
        }
        finally
        {
            Log.Shutdown();
            Directory.Delete(logs, recursive: true);
        }
    }
}
