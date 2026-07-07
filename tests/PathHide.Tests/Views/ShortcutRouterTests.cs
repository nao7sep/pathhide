using System;
using System.Collections.Generic;
using PathHide.Models;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class ShortcutRouterTests
{
    private static MainWindowViewModel NewViewModel()
    {
        var settingsStore = new FakeJsonStore<AppSettings>();
        return new MainWindowViewModel(
            new FakeVisibilityService(),
            new FakeJsonStore<List<PathEntry>>(),
            settingsStore,
            settingsStore.Load());
    }

    [Theory]
    [InlineData(ShortcutAction.HideSelected)]
    [InlineData(ShortcutAction.ShowSelected)]
    [InlineData(ShortcutAction.ReapplyAll)]
    [InlineData(ShortcutAction.Reload)]
    [InlineData(ShortcutAction.CancelScan)]
    public void CommandFor_ReturnsACommand_ForCommandBackedActions(ShortcutAction action)
    {
        Assert.NotNull(ShortcutRouter.CommandFor(NewViewModel(), action));
    }

    [Theory]
    [InlineData(ShortcutAction.AddFiles)]
    [InlineData(ShortcutAction.AddDirectories)]
    [InlineData(ShortcutAction.OpenSettings)]
    [InlineData(ShortcutAction.ShowShortcuts)]
    public void ViewActions_AreNotCommandBacked(ShortcutAction action)
    {
        Assert.True(ShortcutRouter.IsViewAction(action));
        Assert.Null(ShortcutRouter.CommandFor(NewViewModel(), action));
    }

    [Fact]
    public void EveryShortcutAction_IsRoutedExactlyOnce()
    {
        // Guards against the old `default: return false` silently no-oping a newly-added action:
        // every ShortcutAction must be exactly one of command-backed or window-handled — never
        // unrouted (neither) and never ambiguous (both).
        var vm = NewViewModel();
        foreach (var action in Enum.GetValues<ShortcutAction>())
        {
            var hasCommand = ShortcutRouter.CommandFor(vm, action) is not null;
            var isViewAction = ShortcutRouter.IsViewAction(action);
            Assert.True(hasCommand ^ isViewAction, $"{action} must be routed exactly once");
        }
    }
}
