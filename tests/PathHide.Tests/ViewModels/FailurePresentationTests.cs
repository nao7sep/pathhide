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
            FailurePresentation.StartupRefused([StartupRefusal.From(new UnreadableStoreException("paths", "/r/paths.json", error))!]),
            FailurePresentation.SettingsSave(new UnreadableStoreException("settings", "/r/config.json", error)),
            FailurePresentation.RecordsOff(),
            FailurePresentation.BackupsOff(),
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
        Assert.Contains("/home/u/.pathhide/paths.json", English.Of(FailurePresentation.PathListSave(error)), StringComparison.Ordinal);
    }

    [Fact]
    public void APathListThatCouldNotBeOpenedSaysToCheckAccessAndNamesTheFile()
    {
        var error = new UnreadableStoreException("paths", "/home/u/.pathhide/paths.json", new IOException(Hostile));

        Assert.Equal("failure.pathListUnreadableAccess", FailurePresentation.PathListSave(error).Key);
        var text = English.Of(FailurePresentation.PathListSave(error));
        Assert.Contains("/home/u/.pathhide/paths.json", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Hostile, text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableSettingsFileNamesTheFileLeftInPlace()
    {
        var error = new UnreadableStoreException("settings", "/home/u/.pathhide/config.json", new System.Text.Json.JsonException(Hostile));
        var text = English.Of(FailurePresentation.SettingsSave(error));

        Assert.Contains("/home/u/.pathhide/config.json", text, StringComparison.Ordinal);
        Assert.Contains("does not save over it", text, StringComparison.Ordinal);
    }

    // One window names every refused file with why, in the order found, and says what to do.
    [Fact]
    public void TheLaunchStopNamesEveryRefusedFileAndWhy()
    {
        var text = English.Of(FailurePresentation.StartupRefused(
        [
            new StartupRefusal("/d/config.json", RefusalReason.Unreadable),
            new StartupRefusal("/d/state.json", RefusalReason.Newer),
            new StartupRefusal("/d/paths.json", RefusalReason.Inaccessible),
        ]));

        Assert.Contains("/d/config.json could not be read", text, StringComparison.Ordinal);
        Assert.Contains("/d/state.json was saved by a newer version", text, StringComparison.Ordinal);
        Assert.Contains("/d/paths.json could not be opened", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("config.json", StringComparison.Ordinal) < text.IndexOf("state.json", StringComparison.Ordinal));
        Assert.Contains("start that version of PathHide", text, StringComparison.Ordinal);
        Assert.Contains("move it out of the PathHide data folder", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SideStoreNoticesNameTheFileLeftUnchanged()
    {
        Assert.Contains(StorageRoot.RecordsFile, English.Of(FailurePresentation.RecordsOff()), StringComparison.Ordinal);
        Assert.Contains(StorageRoot.LogsDirectory, English.Of(FailurePresentation.RecordsOff()), StringComparison.Ordinal);
        Assert.Contains("backups.sqlite3", English.Of(FailurePresentation.BackupsOff()), StringComparison.Ordinal);
    }
}
