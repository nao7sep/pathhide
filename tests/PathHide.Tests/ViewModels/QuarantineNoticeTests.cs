using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using PathHide.I18n;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using PathHide.Tests.Fakes;
using PathHide.Tests.I18n;
using PathHide.Tests.Storage;
using PathHide.ViewModels;
using Xunit;

namespace PathHide.Tests.ViewModels;

/// <summary>
/// The view model's quarantine notices. They record into the process-wide
/// <see cref="QuarantineJournal"/>, so they share the storage tests' collection, where nothing
/// else runs beside them to drain it.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class QuarantineNoticeTests : IDisposable
{
    public QuarantineNoticeTests() => QuarantineJournal.Drain();

    public void Dispose() => QuarantineJournal.Drain();

    [Fact]
    public async Task Reload_WhenThePathListIsUnreadable_KeepsTheRowsAndNamesTheFileLeftInPlace()
    {
        // Mid-session there is nothing to halt: the rows already shown are the
        // last good state, so they stay rather than being replaced by an empty
        // list, and the user is told which file was left as it is.
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = MainWindowViewModelTests.CreateViewModel(visibility, paths);
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/keep-me" });
        Assert.Single(vm.Rows);

        (Message Title, Message Body)? shown = null;
        vm.ShowNoticeAsync = (title, body) =>
        {
            shown = (title, body);
            return Task.CompletedTask;
        };
        paths.LoadException = new UnreadableStoreException("paths", "/home/u/.pathhide/paths.json", new System.Text.Json.JsonException());

        await ((IAsyncRelayCommand)vm.ReloadCommand).ExecuteAsync(null);

        Assert.Equal("/keep-me", Assert.Single(vm.Rows).Path);
        Assert.Equal("quarantine.pathListTitle", shown!.Value.Title.Key);
        Assert.Contains("/home/u/.pathhide/paths.json", English.Of(shown.Value.Body), StringComparison.Ordinal);
        Assert.Empty(QuarantineJournal.Drain());
    }

    [Fact]
    public async Task Reload_WhenANewerVersionWroteThePathList_KeepsTheRowsAndNamesTheFile()
    {
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = MainWindowViewModelTests.CreateViewModel(new FakeVisibilityService(), paths);
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/keep-me" });

        (Message Title, Message Body)? shown = null;
        vm.ShowNoticeAsync = (title, body) =>
        {
            shown = (title, body);
            return Task.CompletedTask;
        };
        paths.LoadException = new NewerFormatException("/home/u/.pathhide/paths.json", 2, FormatVersions.PathList);

        await ((IAsyncRelayCommand)vm.ReloadCommand).ExecuteAsync(null);

        Assert.Equal("/keep-me", Assert.Single(vm.Rows).Path);
        Assert.Equal("quarantine.pathListTitle", shown!.Value.Title.Key);
        Assert.Contains("/home/u/.pathhide/paths.json", English.Of(shown.Value.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryApplySettings_FailureStillReportsAConfigSetAsideDuringTheSave()
    {
        var settingsStore = new FakeSettingsStore { ThrowOnSave = true };
        var vm = new MainWindowViewModel(
            new BoundedVisibility(new FakeVisibilityService()), new FakeJsonStore<List<PathEntry>>(), settingsStore,
            settingsStore.Load().Value, new FakeJsonStore<AppState>(), new AppState());
        (Message Title, Message Body)? shown = null;
        vm.ShowNoticeAsync = (title, body) =>
        {
            shown = (title, body);
            return Task.CompletedTask;
        };
        // The save's load finds config unreadable and sets it aside before the write fails.
        QuarantineJournal.Record("settings", "/home/u/.pathhide/config-20261002-000000-000-utc.invalid");

        var failure = await vm.TryApplySettingsAsync(Languages.System, string.Empty, hiddenAndSystem: false, ThemePreference.Dark);

        Assert.Contains("Settings could not be saved", English.Of(failure));
        Assert.Equal("quarantine.settingsTitle", shown!.Value.Title.Key);
        Assert.Contains("/home/u/.pathhide/config-20261002-000000-000-utc.invalid", English.Of(shown.Value.Body), StringComparison.Ordinal);
        Assert.Empty(QuarantineJournal.Drain());
    }
}
