using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using PathHide.I18n;
using PathHide.Models;
using PathHide.Storage;
using PathHide.Tests.Fakes;
using PathHide.Tests.I18n;
using Xunit;

namespace PathHide.Tests.ViewModels;

/// <summary>
/// Reload of a path list that cannot be used mid-session: there is nothing to halt, so the rows already
/// shown stay as the last good state and the user is told which file was left as it is.
/// </summary>
public sealed class PathListReloadNoticeTests
{
    private const string ListPath = "/home/u/.pathhide/paths.json";

    [Fact]
    public async Task Reload_WhenThePathListIsInvalid_KeepsTheRowsAndSaysToRepairTheFile()
    {
        var (vm, paths, shown) = await CreateWithOneRowAsync();
        paths.LoadException = new UnreadableStoreException("paths", ListPath, new System.Text.Json.JsonException());

        await ((IAsyncRelayCommand)vm.ReloadCommand).ExecuteAsync(null);

        Assert.Equal("/keep-me", Assert.Single(vm.Rows).Path);
        Assert.Equal("failure.pathListReloadTitle", shown.Value!.Value.Title.Key);
        Assert.Equal("failure.pathListUnreadable", shown.Value!.Value.Body.Key);
        Assert.Contains(ListPath, English.Of(shown.Value!.Value.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reload_WhenThePathListCannotBeOpened_KeepsTheRowsAndSaysToCheckAccess()
    {
        var (vm, paths, shown) = await CreateWithOneRowAsync();
        paths.LoadException = new UnreadableStoreException("paths", ListPath, new UnauthorizedAccessException());

        await ((IAsyncRelayCommand)vm.ReloadCommand).ExecuteAsync(null);

        Assert.Equal("/keep-me", Assert.Single(vm.Rows).Path);
        Assert.Equal("failure.pathListUnreadableAccess", shown.Value!.Value.Body.Key);
        Assert.Contains(ListPath, English.Of(shown.Value!.Value.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reload_WhenANewerVersionWroteThePathList_KeepsTheRowsAndNamesTheFile()
    {
        var (vm, paths, shown) = await CreateWithOneRowAsync();
        paths.LoadException = new NewerFormatException(ListPath, 2, FormatVersions.PathList);

        await ((IAsyncRelayCommand)vm.ReloadCommand).ExecuteAsync(null);

        Assert.Equal("/keep-me", Assert.Single(vm.Rows).Path);
        Assert.Equal("failure.pathListReloadTitle", shown.Value!.Value.Title.Key);
        Assert.Contains(ListPath, English.Of(shown.Value!.Value.Body), StringComparison.Ordinal);
    }

    private sealed class Shown
    {
        public (Message Title, Message Body)? Value { get; set; }
    }

    private static async Task<(PathHide.ViewModels.MainWindowViewModel Vm, FakeJsonStore<List<PathEntry>> Paths, Shown Shown)>
        CreateWithOneRowAsync()
    {
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = MainWindowViewModelTests.CreateViewModel(new FakeVisibilityService(), paths);
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/keep-me" });
        Assert.Single(vm.Rows);

        var shown = new Shown();
        vm.ShowNoticeAsync = (title, body) =>
        {
            shown.Value = (title, body);
            return Task.CompletedTask;
        };
        return (vm, paths, shown);
    }
}
