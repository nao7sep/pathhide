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
    public async Task Reload_WhenThePathListIsUnreadable_KeepsTheRowsOnScreen()
    {
        // Mid-session there is nothing to halt: the rows already shown are the
        // last good state, so they stay rather than being replaced by an empty
        // list, and the user is told what happened.
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = MainWindowViewModelTests.CreateViewModel(visibility, paths);
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/keep-me" });
        Assert.Single(vm.Rows);

        var told = false;
        vm.ShowNoticeAsync = (_, _) => { told = true; return Task.CompletedTask; };
        paths.LoadIsUnreadable = true;
        QuarantineJournal.Record("paths", "/r/paths-x.invalid");

        await ((IAsyncRelayCommand)vm.ReloadCommand).ExecuteAsync(null);

        Assert.Single(vm.Rows);
        Assert.Equal("/keep-me", vm.Rows[0].Path);
        Assert.True(told);
    }

    [Fact]
    public async Task Reload_WhenTheStoreWasQuarantined_TellsTheUser()
    {
        // The startup drain runs once, in the window's Opened handler. A load
        // that quarantines afterwards - pressing Reload on a paths.json edited
        // into invalid JSON - emptied every row with no notice, no explanation
        // and no safe recovery guidance for the copy that was set aside.
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = MainWindowViewModelTests.CreateViewModel(visibility, paths);

        (Message Title, Message Body)? shown = null;
        vm.ShowNoticeAsync = (title, body) =>
        {
            shown = (title, body);
            return Task.CompletedTask;
        };

        // The store finds the file unreadable on this load and sets it aside.
        QuarantineJournal.Record("paths", "/home/u/.pathhide/paths-20260821-000000-000-utc.invalid");

        await ((IAsyncRelayCommand)vm.ReloadCommand).ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal("quarantine.pathListTitle", shown!.Value.Title.Key);
        Assert.Contains("session log", English.Of(shown.Value.Body));
        Assert.DoesNotContain("/home/u/.pathhide", English.Of(shown.Value.Body), StringComparison.Ordinal);
        // Drained, so a second reload does not repeat it.
        Assert.Empty(QuarantineJournal.Drain());
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
        Assert.Empty(QuarantineJournal.Drain());
    }
}
