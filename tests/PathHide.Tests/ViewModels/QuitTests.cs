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
/// running is waited for, one that fails or does not land in time stops a quit the user started with
/// Retry or Quit anyway, and the session ending never asks. Bounds run on a controlled clock.
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
            new BoundedVisibility(_visibility), _paths, _settings, _settings.Load().Value,
            state ?? new FakeJsonStore<AppState>(), new AppState())
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

    [Fact(Timeout = 10_000)]
    public async Task A_save_still_running_at_the_bound_stops_the_quit_then_Retry_waits_again()
    {
        using var gate = new ManualResetEventSlim(false);
        _paths.SaveGate = gate;
        _answers.Enqueue(() => UnsavedQuitChoice.Retry);
        var vm = CreateViewModel();
        var remove = await StartRemoveAsync(vm);

        var quit = vm.QuitAsync();
        _clock.Advance(Bound - TimeSpan.FromTicks(1));
        Assert.False(quit.IsCompleted);
        Assert.Empty(_asked);

        _clock.Advance(TimeSpan.FromTicks(1));
        await WaitUntilAsync(() => _asked.Count == 1);
        Assert.Equal(
            ["Your latest change is still being saved.", "If you quit now, your changes will be lost."],
            _asked[0].Select(English.Of));

        // Retry waits again, and the save lands within it.
        gate.Set();
        Assert.True(await quit.WaitAsync(Guard, TestContext.Current.CancellationToken));
        await remove;
        Assert.Empty(_paths.Value);
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

    [Fact(Timeout = 10_000)]
    public async Task A_placement_save_holds_a_close_for_its_bound_only()
    {
        using var gate = new ManualResetEventSlim(false);
        var state = new FakeJsonStore<AppState> { SaveGate = gate };
        var vm = CreateViewModel(state);

        var save = Task.Run(() => vm.SaveWindowPlacement(1, 2, 800, 600, maximized: false),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => _clock.HasTimers);
        _clock.Advance(vm.PlacementSaveBound);

        await Assert.ThrowsAsync<TimeoutException>(() => save.WaitAsync(Guard, TestContext.Current.CancellationToken));
        gate.Set();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Guard;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold in time.");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
