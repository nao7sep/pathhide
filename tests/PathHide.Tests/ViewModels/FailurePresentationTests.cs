using System;
using System.IO;
using PathHide.ViewModels;
using Xunit;

namespace PathHide.Tests.ViewModels;

public sealed class FailurePresentationTests
{
    private const string Hostile = "EACCES Error invoking remote method IPC /private/tmp/hostile-sentinel";

    [Fact]
    public void ArbitraryDiagnosticsDoNotReachPresentation()
    {
        var error = new IOException(Hostile, new InvalidOperationException("root cause"));

        var messages = new[]
        {
            FailurePresentation.SettingsSave(error),
            FailurePresentation.PathListSave(error),
            FailurePresentation.Scan(error),
            FailurePresentation.PathPicker(error),
            FailurePresentation.WindowAction(error),
            FailurePresentation.StartupStorage(),
            FailurePresentation.Startup(),
            FailurePresentation.PathListStartup(),
        };

        Assert.All(messages, message => Assert.DoesNotContain(Hostile, message, StringComparison.Ordinal));
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void PermissionFailuresUseStructuredRecovery()
    {
        var error = new UnauthorizedAccessException(Hostile);

        Assert.Contains("writable", FailurePresentation.SettingsSave(error), StringComparison.Ordinal);
        Assert.Contains("writable", FailurePresentation.PathListSave(error), StringComparison.Ordinal);
        Assert.Contains("permission", FailurePresentation.Scan(error), StringComparison.Ordinal);
    }
}
