using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using Xunit;

namespace PathHide.Tests.ViewModels;

public sealed class BoundedStoreWorkViewModelTests
{
    private static MainWindowViewModel Create(FakeJsonStore<List<PathEntry>> paths, FakeSettingsStore settings) =>
        new(new BoundedVisibility(new FakeVisibilityService()), paths, settings, settings.Load().Value,
            new FakeJsonStore<AppState>(), new AppState())
        { StorageWaitBound = TimeSpan.FromMilliseconds(150), ShutdownBound = TimeSpan.FromMilliseconds(200) };

    [Fact]
    public async Task Reload_timeout_keeps_rows_and_late_read_never_replaces_newer_state()
    {
        using var gate = new ManualResetEventSlim();
        var paths = new FakeJsonStore<List<PathEntry>> { Value = [new() { Path = "/original", DesiredVisibility = DesiredVisibility.Hidden }] };
        var vm = Create(paths, new FakeSettingsStore());
        vm.LoadPersistedState();
        paths.LoadGate = gate;
        try
        {
            await ((IAsyncRelayCommand)vm.ReloadCommand).ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal("/original", Assert.Single(vm.Rows).Path);
            paths.Value = [new() { Path = "/late", DesiredVisibility = DesiredVisibility.Hidden }];
        }
        finally
        {
            gate.Set();
            // Admission joins the abandoned read before the gate can be disposed.
            await vm.AddPathsCommand.ExecuteAsync(new[] { "/new" });
            await vm.ScanTask;
        }
        Assert.Equal(2, vm.Rows.Count);
        Assert.Contains(vm.Rows, row => row.Path == "/original");
        Assert.DoesNotContain(vm.Rows, row => row.Path == "/late");
    }

    [Fact]
    public async Task Cancel_releases_a_reload_wait_and_keeps_the_current_list()
    {
        using var gate = new ManualResetEventSlim();
        var paths = new FakeJsonStore<List<PathEntry>>
        {
            Value = [new() { Path = "/original", DesiredVisibility = DesiredVisibility.Hidden }],
        };
        var vm = Create(paths, new FakeSettingsStore());
        vm.LoadPersistedState();
        paths.LoadGate = gate;
        try
        {
            var reload = vm.ReloadCommand.ExecuteAsync(null);
            Assert.True(vm.IsApplying);
            vm.CancelCommand.Execute(null);
            await reload.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal("/original", Assert.Single(vm.Rows).Path);
            Assert.False(vm.IsApplying);
        }
        finally
        {
            gate.Set();
            paths.LoadGate = null;
            await vm.ReloadCommand.ExecuteAsync(null);
            await vm.ScanTask;
        }
    }

    [Fact]
    public async Task Settings_timeout_never_publishes_late_and_successor_saves_after_the_physical_tail()
    {
        using var gate = new ManualResetEventSlim();
        var settings = new FakeSettingsStore { SaveGate = gate };
        var vm = Create(new FakeJsonStore<List<PathEntry>>(), settings);
        try
        {
            var failure = await vm.TryApplySettingsAsync("en", "", false, ThemePreference.Dark);
            Assert.NotNull(failure);
            Assert.Equal(ThemePreference.System, vm.Theme);
            Assert.True(vm.HasWorkToFinish);
            settings.SaveGate = null;
        }
        finally
        {
            gate.Set();
            Assert.Null(await vm.TryApplySettingsAsync("en", "", false, ThemePreference.Light));
        }
        Assert.Equal(ThemePreference.Light, vm.Theme);
        Assert.Equal(ThemePreference.Light, settings.LastSaved!.Theme);
    }

    [Fact]
    public async Task Retrying_the_same_settings_after_a_late_commit_saves_them_again()
    {
        using var gate = new ManualResetEventSlim();
        var settings = new FakeSettingsStore { SaveGate = gate };
        var vm = Create(new FakeJsonStore<List<PathEntry>>(), settings);
        try
        {
            Assert.NotNull(await vm.TryApplySettingsAsync("en", "", false, ThemePreference.Dark));
            Assert.Equal(ThemePreference.System, vm.Theme);
        }
        finally
        {
            gate.Set();
            Assert.Null(await vm.TryApplySettingsAsync("en", "", false, ThemePreference.Dark));
        }
        Assert.Equal(ThemePreference.Dark, vm.Theme);
        // The timed-out save may have landed, so memory no longer proves what is on disk and the retry
        // writes; the real store then skips identical bytes.
        Assert.Equal(2, settings.SaveCount);
    }

    [Fact]
    public async Task Keeping_the_original_settings_after_a_late_commit_writes_them_back()
    {
        using var gate = new ManualResetEventSlim();
        var settings = new FakeSettingsStore { SaveGate = gate };
        var vm = Create(new FakeJsonStore<List<PathEntry>>(), settings);
        try
        {
            Assert.NotNull(await vm.TryApplySettingsAsync("en", "", false, ThemePreference.Dark));
        }
        finally
        {
            gate.Set();
        }

        // Saving the settings the app still holds must not be skipped as unchanged: the late commit put
        // Dark on disk.
        Assert.Null(await vm.TryApplySettingsAsync("en", "", false, ThemePreference.System));

        Assert.Equal(ThemePreference.System, settings.Value.Theme);
        Assert.Equal(ThemePreference.System, vm.Theme);
    }

    [Fact]
    public async Task Quit_question_failure_keeps_open_and_releases_the_mutation_gate()
    {
        using var gate = new ManualResetEventSlim();
        var settings = new FakeSettingsStore { SaveGate = gate };
        var vm = Create(new FakeJsonStore<List<PathEntry>>(), settings);
        try
        {
            Assert.NotNull(await vm.TryApplySettingsAsync("en", "", false, ThemePreference.Dark));
            vm.AskUnsavedAtQuitAsync = _ => throw new InvalidOperationException("presentation failed");
            Assert.False(await vm.QuitAsync().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        }
        finally
        {
            gate.Set();
            await vm.TryApplySettingsAsync("en", "", false, ThemePreference.Light);
        }
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/after-quit" }).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal("/after-quit", Assert.Single(vm.Rows).Path);
    }
}
