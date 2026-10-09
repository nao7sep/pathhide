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
            FailurePresentation.PathListStartup(new UnreadableStoreException("paths", "/r/paths.json", error)),
        };

        Assert.All(messages, message => Assert.DoesNotContain(Hostile, English.Of(message), StringComparison.Ordinal));
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void AnUnexpectedStartupFailureNamesWhereItsDetailsAre()
    {
        var text = English.Of(FailurePresentation.Startup());

        Assert.Contains(StorageRoot.RecordsFile, text, StringComparison.Ordinal);
        Assert.Contains(StorageRoot.LogsDirectory, text, StringComparison.Ordinal);
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
    public void SaveTimeoutsDescribeTheUnknownOutcomeWithoutExposingDiagnostics()
    {
        var error = new TimeoutException(Hostile, new IOException("root cause"));
        var paths = FailurePresentation.PathListSave(error);
        var settings = FailurePresentation.SettingsSave(error);

        Assert.Equal("failure.pathListSaveTimeout", paths.Key);
        Assert.Equal("failure.settingsSaveTimeout", settings.Key);
        Assert.Equal("Saving the path list took too long and may still finish. The current list is still in use. Use Reload to check before trying again.", English.Of(paths));
        Assert.Equal("Saving settings took too long and may still finish. Your draft is still here; try Save again.", English.Of(settings));
        Assert.DoesNotContain(Hostile, English.Of(paths), StringComparison.Ordinal);
        Assert.DoesNotContain(Hostile, English.Of(settings), StringComparison.Ordinal);
        Assert.Equal("failure.pathListSave", FailurePresentation.PathListSave(new IOException("timeout " + Hostile)).Key);
        Assert.Equal("failure.settingsSave", FailurePresentation.SettingsSave(new IOException("timeout " + Hostile)).Key);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public void APathListSaveRefusedOverANewerFileNamesTheFile()
    {
        var error = new NewerFormatException("/home/u/.pathhide/paths.json", 2, 1);

        Assert.Equal("failure.newerStore", FailurePresentation.PathListSave(error).Key);
        Assert.Contains("/home/u/.pathhide/paths.json", English.Of(FailurePresentation.NewerStore(error)), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadablePathListNamesTheFileLeftInPlace()
    {
        var error = new UnreadableStoreException("paths", "/home/u/.pathhide/paths.json", new System.Text.Json.JsonException(Hostile));

        Assert.Equal("failure.pathListUnreadable", FailurePresentation.PathListSave(error).Key);
        Assert.Equal("failure.pathListStartup", FailurePresentation.PathListStartup(error).Key);
        Assert.Contains("/home/u/.pathhide/paths.json", English.Of(FailurePresentation.PathListSave(error)), StringComparison.Ordinal);
        Assert.Contains("/home/u/.pathhide/paths.json", English.Of(FailurePresentation.PathListStartup(error)), StringComparison.Ordinal);
    }

    [Fact]
    public void APathListThatCouldNotBeOpenedSaysToCheckAccessAndNamesTheFile()
    {
        var error = new UnreadableStoreException("paths", "/home/u/.pathhide/paths.json", new IOException(Hostile));

        Assert.Equal("failure.pathListUnreadableAccess", FailurePresentation.PathListSave(error).Key);
        Assert.Equal("failure.pathListStartupAccess", FailurePresentation.PathListStartup(error).Key);
        var text = English.Of(FailurePresentation.PathListStartup(error));
        Assert.Contains("/home/u/.pathhide/paths.json", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Hostile, text, StringComparison.Ordinal);
    }
}
