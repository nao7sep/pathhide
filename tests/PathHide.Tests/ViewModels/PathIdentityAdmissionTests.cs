using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using Xunit;

namespace PathHide.Tests.ViewModels;

public sealed class PathIdentityAdmissionTests
{
    private static PathEntry Entry(string path) => new() { Path = path, DesiredVisibility = DesiredVisibility.Hidden };

    private static MainWindowViewModel Model(BoundedVisibility visibility, FakeJsonStore<List<PathEntry>> paths) =>
        new(visibility, paths, new FakeSettingsStore(), new AppSettings(), new FakeJsonStore<AppState>(), new AppState(), paths.Value)
        { IdentityWaitBound = TimeSpan.FromMilliseconds(100) };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_identity_does_not_block_admission_or_local_saves_and_never_publishes_late(bool reload)
    {
        using var release = new ManualResetEventSlim();
        var service = new FakeVisibilityService { ResolveGate = release };
        var visibility = new BoundedVisibility(service);
        var paths = new FakeJsonStore<List<PathEntry>> { Value = [Entry("/a"), Entry("/b")] };
        var vm = Model(visibility, paths);
        try
        {
            var admission = reload ? vm.ReloadCommand.ExecuteAsync(null) : vm.ValidateLoadedPathsAsync();
            await service.ResolveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await admission.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(release.IsSet);
            Assert.Equal(2, vm.Rows.Count);
            vm.Rows[1].IsSelected = true;
            await vm.RemoveSelectedCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, paths.SaveCount);
            Assert.Equal("/a", Assert.Single(vm.Rows).Path);
            service.Aliases["/a"] = "/same";
            service.Aliases["/b"] = "/same";
        }
        finally
        {
            release.Set();
            // Join the physical resolver via its existing owner before disposing the fixture gate.
            await visibility.ResolvePathAsync("/a", CancellationToken.None);
            await vm.ScanTask;
        }
        Assert.Equal("/a", Assert.Single(vm.Rows).Path);
    }

    [Fact]
    public async Task Known_alias_conflict_rejects_reload_and_cannot_be_overwritten_by_the_kept_rows()
    {
        var service = new FakeVisibilityService();
        service.Aliases["/a"] = "/same";
        service.Aliases["/b"] = "/same";
        var paths = new FakeJsonStore<List<PathEntry>> { Value = [Entry("/original")] };
        var vm = Model(new BoundedVisibility(service), paths);
        paths.Value = [Entry("/a"), Entry("/b")];
        await vm.ReloadCommand.ExecuteAsync(null);
        Assert.Equal("/original", Assert.Single(vm.Rows).Path);
        vm.Rows[0].IsSelected = true;
        await vm.RemoveSelectedCommand.ExecuteAsync(null);
        Assert.Equal(0, paths.SaveCount);
        Assert.Equal(2, paths.Value.Count);
        Assert.Equal("/original", Assert.Single(vm.Rows).Path);

        paths.Value = [Entry("/a")];
        await vm.ReloadCommand.ExecuteAsync(null);
        vm.Rows[0].IsSelected = true;
        await vm.RemoveSelectedCommand.ExecuteAsync(null);
        Assert.Empty(vm.Rows);
        Assert.Equal(1, paths.SaveCount);
    }

    [Fact]
    public async Task Known_alias_conflict_rejects_prepared_startup_values()
    {
        var service = new FakeVisibilityService();
        service.Aliases["/a"] = "/same";
        service.Aliases["/b"] = "/same";
        var vm = Model(new BoundedVisibility(service), new FakeJsonStore<List<PathEntry>> { Value = [Entry("/a"), Entry("/b")] });
        await Assert.ThrowsAsync<UnreadableStoreException>(() => vm.ValidateLoadedPathsAsync());
    }
}
