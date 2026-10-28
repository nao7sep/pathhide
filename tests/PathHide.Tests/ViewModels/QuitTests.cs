using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using PathHide.I18n;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using PathHide.Tests.I18n;
using PathHide.ViewModels;
using Xunit;

namespace PathHide.Tests.ViewModels;

/// <summary>
/// The quit's waits for the user's own saves (unsaved-edits-conventions, Quitting): a save still
/// running is waited for, one that fails stops a quit the user started with Retry or Quit anyway, and
/// the session ending never asks. The exit's bound runs on a controlled clock.
/// </summary>
public sealed class QuitTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    private readonly ManualClock _clock = new();
    private readonly FakeVisibilityService _visibility = new();
    private readonly FakeSettingsStore _settings = new();
    private readonly FakeJsonStore<List<PathEntry>> _paths = new()
    {
        Value = [new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Hidden }],
    };

    private readonly List<IReadOnlyList<Message>> _asked = [];
    // Each answer runs as the question is asked, so a test can mend the store before a Retry.
    private readonly Queue<Func<UnsavedQuitChoice>> _answers = new();

    private MainWindowViewModel CreateViewModel(FakeJsonStore<AppState>? state = null)
    {
        var vm = new MainWindowViewModel(
            new VisibilityCalls(_visibility), _paths, _settings, _settings.Load().Value,
            state ?? new FakeJsonStore<AppState>(), new AppState(), _paths.Load().Value)
        {
            Clock = _clock,
            ShutdownBound = Bound,
            AskUnsavedAtQuitAsync = lines =>
            {
                _asked.Add(lines);
                return Task.FromResult(_answers.Dequeue()());
            },
        };
        vm.Initialize();
        return vm;
    }

    // Starts a Remove of every row and returns once its save is running, held by the store's gate.
    private static async Task<Task> StartRemoveAsync(MainWindowViewModel vm)
    {
        await vm.ScanTask;
        foreach (var row in vm.Rows)
            row.IsSelected = true;
        var remove = ((IAsyncRelayCommand)vm.RemoveSelectedCommand).ExecuteAsync(null);
        Assert.True(vm.HasWorkToFinish);
        return remove;
    }

    [Fact(Timeout = 10_000)]
    public async Task A_reload_released_after_the_quit_begins_changes_no_file_or_attribute()
    {
        var vm = CreateViewModel();
        await vm.ScanTask;
        using var gate = new ManualResetEventSlim(false);
        _paths.LoadGate = gate;
        var reload = ((IAsyncRelayCommand)vm.ReloadCommand).ExecuteAsync(null);

        var quit = vm.QuitAsync();
        gate.Set();
        await reload;

        Assert.True(await quit.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Equal(0, _paths.SaveCount);
        Assert.Empty(_visibility.Hidden);
        Assert.Empty(_visibility.Shown);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_save_still_running_holds_the_quit_until_it_lands()
    {
        using var gate = new ManualResetEventSlim(false);
        _paths.SaveGate = gate;
        var vm = CreateViewModel();
        var remove = await StartRemoveAsync(vm);

        var quit = vm.QuitAsync();
        Assert.False(quit.IsCompleted);

        gate.Set();
        Assert.True(await quit.WaitAsync(Guard, TestContext.Current.CancellationToken));
        await remove;

        Assert.Empty(_paths.Value);
        Assert.Empty(_asked);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_save_that_fails_as_the_quit_waits_is_saved_again_on_Retry()
    {
        using var gate = new ManualResetEventSlim(false);
        _paths.SaveGate = gate;
        _paths.ThrowOnSave = true;
        // The Retry finds the store working again.
        _answers.Enqueue(() =>
        {
            _paths.ThrowOnSave = false;
            return UnsavedQuitChoice.Retry;
        });
        var vm = CreateViewModel();
        var remove = await StartRemoveAsync(vm);

        var quit = vm.QuitAsync();
        gate.Set();
        await remove;

        Assert.True(await quit.WaitAsync(Guard, TestContext.Current.CancellationToken));
        var lines = Assert.Single(_asked);
        Assert.Equal(
            ["The path list could not be saved. Your existing list is unchanged; try again.",
             "If you quit now, your changes will be lost."],
            lines.Select(English.Of));
        Assert.Empty(_paths.Value);
    }

    [Fact(Timeout = 10_000)]
    public async Task Quit_anyway_exits_with_the_change_unsaved()
    {
        _paths.ThrowOnSave = true;
        using var gate = new ManualResetEventSlim(false);
        _paths.SaveGate = gate;
        _answers.Enqueue(() => UnsavedQuitChoice.QuitAnyway);
        var vm = CreateViewModel();
        var remove = await StartRemoveAsync(vm);

        var quit = vm.QuitAsync();
        gate.Set();
        await remove;

        Assert.True(await quit.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Single(_asked);
        Assert.Equal(0, _paths.SaveCount);
    }

    [Fact(Timeout = 10_000)]
    public async Task Dismissing_the_question_keeps_the_app_open_and_working()
    {
        _paths.ThrowOnSave = true;
        using var gate = new ManualResetEventSlim(false);
        _paths.SaveGate = gate;
        _answers.Enqueue(() => UnsavedQuitChoice.KeepOpen);
        var vm = CreateViewModel();
        var remove = await StartRemoveAsync(vm);

        var quit = vm.QuitAsync();
        gate.Set();
        await remove;
        Assert.False(await quit.WaitAsync(Guard, TestContext.Current.CancellationToken));

        // The failed change was dropped, as any failed save is, and commands run again.
        _paths.ThrowOnSave = false;
        await ((IAsyncRelayCommand)vm.RemoveSelectedCommand).ExecuteAsync(null);
        Assert.Empty(_paths.Value);

        // A later quit has nothing left to ask about.
        Assert.True(await vm.QuitAsync().WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Single(_asked);
    }

    // The exit is bounded: a save still running at the bound has not failed, so the quit asks nothing,
    // and the save's atomic replacement leaves either the old list or the new one.
    [Fact]
    public async Task A_save_still_running_at_the_bound_does_not_hold_the_quit()
    {
        using var gate = new ManualResetEventSlim(false);
        _paths.SaveGate = gate;
        var vm = CreateViewModel();
        var remove = await StartRemoveAsync(vm);

        var quit = vm.QuitAsync();
        _clock.Advance(Bound - TimeSpan.FromTicks(1));
        Assert.False(quit.IsCompleted);

        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(await quit);
        Assert.Empty(_asked);

        gate.Set();
        await remove;
    }

    [Fact(Timeout = 10_000)]
    public async Task A_settings_save_is_waited_for_and_a_failure_offers_Retry()
    {
        using var gate = new ManualResetEventSlim(false);
        _settings.SaveGate = gate;
        _settings.ThrowOnSave = true;
        _answers.Enqueue(() =>
        {
            _settings.ThrowOnSave = false;
            return UnsavedQuitChoice.Retry;
        });
        var vm = CreateViewModel();
        await vm.ScanTask;

        var save = vm.TryApplySettingsAsync(Languages.System, string.Empty, hiddenAndSystem: true, ThemePreference.System);
        Assert.True(vm.HasWorkToFinish);
        var quit = vm.QuitAsync();
        gate.Set();
        Assert.NotNull(await save);

        Assert.True(await quit.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Equal(
            "Settings could not be saved. Your changes are still here; try again.",
            English.Of(Assert.Single(_asked)[0]));
        Assert.Equal(WindowsHideMode.HiddenAndSystem, _settings.Value.WindowsHideMode);
    }

    [Fact(Timeout = 10_000)]
    public async Task The_session_ending_never_asks_and_ends_within_the_bound()
    {
        using var gate = new ManualResetEventSlim(false);
        _paths.SaveGate = gate;
        var vm = CreateViewModel();
        var remove = await StartRemoveAsync(vm);

        var end = vm.EndSessionAsync();
        _clock.Advance(Bound);
        await end.WaitAsync(Guard, TestContext.Current.CancellationToken);

        Assert.Empty(_asked);
        gate.Set();
        await remove;
    }

    // Starts Reapply All and returns once its write is running, held by the visibility gate.
    private async Task<Task> StartReapplyAsync(MainWindowViewModel vm, ManualResetEventSlim gate)
    {
        await vm.ScanTask;
        _visibility.WriteGate = gate;
        var reapply = ((IAsyncRelayCommand)vm.ReapplyAllCommand).ExecuteAsync(null);
        await _visibility.WriteEntered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        Assert.True(vm.IsApplying);
        return reapply;
    }

    [Fact(Timeout = 10_000)]
    public async Task A_quit_while_hide_or_show_runs_asks_and_Cancel_leaves_it_running()
    {
        using var gate = new ManualResetEventSlim(false);
        var vm = CreateViewModel();
        var asked = 0;
        vm.ConfirmStopAtQuitAsync = () =>
        {
            asked++;
            return Task.FromResult(false);
        };
        var reapply = await StartReapplyAsync(vm, gate);

        Assert.False(await vm.QuitAsync());

        Assert.Equal(1, asked);
        Assert.True(vm.IsApplying);
        gate.Set();
        await reapply;
        Assert.Equal("/a", Assert.Single(_visibility.Hidden));
        Assert.Empty(_asked);
    }

    [Fact(Timeout = 10_000)]
    public async Task Choosing_Stop_and_quit_stops_the_running_hide_or_show_and_quits()
    {
        using var gate = new ManualResetEventSlim(false);
        var vm = CreateViewModel();
        var asked = 0;
        vm.ConfirmStopAtQuitAsync = () =>
        {
            asked++;
            return Task.FromResult(true);
        };
        var reapply = await StartReapplyAsync(vm, gate);

        var quit = vm.QuitAsync();
        gate.Set();

        Assert.True(await quit.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Equal(1, asked);
        await reapply;
        Assert.False(vm.IsApplying);
    }

    [Fact(Timeout = 10_000)]
    public async Task The_session_ending_stops_a_running_hide_or_show_without_asking()
    {
        using var gate = new ManualResetEventSlim(false);
        var vm = CreateViewModel();
        var asked = 0;
        vm.ConfirmStopAtQuitAsync = () =>
        {
            asked++;
            return Task.FromResult(false);
        };
        var reapply = await StartReapplyAsync(vm, gate);

        var end = vm.EndSessionAsync();
        gate.Set();
        await end.WaitAsync(Guard, TestContext.Current.CancellationToken);

        Assert.Equal(0, asked);
        await reapply;
    }

    [Fact(Timeout = 10_000)]
    public async Task An_apply_after_a_save_the_quit_waited_for_does_not_run()
    {
        using var gate = new ManualResetEventSlim(false);
        _paths.Value = [new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Shown }];
        var vm = CreateViewModel();
        await vm.ScanTask;
        vm.Rows[0].IsSelected = true;
        _paths.SaveGate = gate;

        var hide = ((IAsyncRelayCommand)vm.HideSelectedCommand).ExecuteAsync(null);
        Assert.True(vm.HasWorkToFinish);
        var quit = vm.QuitAsync();
        gate.Set();

        Assert.True(await quit.WaitAsync(Guard, TestContext.Current.CancellationToken));
        await hide;
        Assert.Equal(DesiredVisibility.Hidden, Assert.Single(_paths.Value).DesiredVisibility);
        Assert.Empty(_visibility.Hidden);
    }
}
