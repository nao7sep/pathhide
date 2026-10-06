using System;
using System.IO;
using PathHide.Storage;
using PathHide.Tests.I18n;
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
            FailurePresentation.StartupUnreadable(new UnreadableStoreException("settings", "/r/config.json", error)),
            FailurePresentation.PathListStartup(new UnreadableStoreException("paths", "/r/paths.json", error)),
        };

        Assert.All(messages, message => Assert.DoesNotContain(Hostile, English.Of(message), StringComparison.Ordinal));
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void PermissionFailuresUseStructuredRecovery()
    {
        var error = new UnauthorizedAccessException(Hostile);

        Assert.Contains("writable", English.Of(FailurePresentation.SettingsSave(error)), StringComparison.Ordinal);
        Assert.Contains("writable", English.Of(FailurePresentation.PathListSave(error)), StringComparison.Ordinal);
        Assert.Contains("permission", English.Of(FailurePresentation.Scan(error)), StringComparison.Ordinal);
    }

    [Fact]
    public void ASaveRefusedOverANewerFileNamesTheFile()
    {
        var error = new NewerFormatException("/home/u/.pathhide/paths.json", 2, 1);

        Assert.Equal("failure.newerStore", FailurePresentation.SettingsSave(error).Key);
        Assert.Equal("failure.newerStore", FailurePresentation.PathListSave(error).Key);
        Assert.Contains("/home/u/.pathhide/paths.json", English.Of(FailurePresentation.NewerStore(error)), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadablePathListNamesTheFileLeftInPlace()
    {
        var error = new UnreadableStoreException("paths", "/home/u/.pathhide/paths.json", new IOException(Hostile));

        Assert.Equal("failure.pathListUnreadable", FailurePresentation.PathListSave(error).Key);
        Assert.Contains("/home/u/.pathhide/paths.json", English.Of(FailurePresentation.PathListSave(error)), StringComparison.Ordinal);
        Assert.Contains("/home/u/.pathhide/paths.json", English.Of(FailurePresentation.PathListStartup(error)), StringComparison.Ordinal);
    }

    [Fact]
    public void AStoreThatCouldNotBeSetAsideNamesTheFileLeftInPlace()
    {
        var error = new UnreadableStoreException("settings", "/home/u/.pathhide/config.json", new IOException(Hostile));

        var text = English.Of(FailurePresentation.StartupUnreadable(error));

        Assert.Contains("/home/u/.pathhide/config.json", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Hostile, text, StringComparison.Ordinal);
    }
}
