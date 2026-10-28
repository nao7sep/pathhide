using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PathHide.Backup;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using PathHide.Tests.Fakes;
using PathHide.Tests.Storage;
using PathHide.ViewModels;
using Xunit;

namespace PathHide.Tests.Services;

[Collection(StorageRootEnvironment.CollectionName)]
public sealed class MacPathIdentityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pathhide-identity-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable);

    public MacPathIdentityTests()
    {
        BackupStore.Close();
        Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, _root);
    }

    public void Dispose()
    {
        BackupStore.Close();
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, _previous);
        Directory.Delete(_root, recursive: true);
    }

    private static MainWindowViewModel Model(FakeJsonStore<List<PathEntry>> paths) =>
        new(new VisibilityCalls(new MacVisibilityService()), paths, new FakeSettingsStore(), new AppSettings(),
            new FakeJsonStore<AppState>(), new AppState(), paths.Value);

    [MacOnlyFact]
    public async Task Existing_case_aliases_are_duplicates_including_loaded_alias_spellings()
    {
        var path = Path.Combine(_root, "MixedCase.txt");
        File.WriteAllText(path, "x");
        var alias = path.ToLowerInvariant();
        if (!File.Exists(alias))
            alias = path; // A case-sensitive host still checks normalized and parent aliases.
        var parentAlias = Path.Combine(_root, "parent-alias");
        Directory.CreateSymbolicLink(parentAlias, _root);
        try
        {
            var paths = new FakeJsonStore<List<PathEntry>>
            {
                Value = [new() { Path = alias, DesiredVisibility = DesiredVisibility.Shown }],
            };
            var vm = Model(paths);
            await vm.AddPathsCommand.ExecuteAsync([Path.Combine(parentAlias, "MixedCase.txt")]);
            Assert.Single(vm.Rows);
            Assert.Equal(0, paths.SaveCount);
            Assert.Equal(alias, paths.Value[0].Path); // Existing stored spelling remains authoritative.
        }
        finally { Directory.Delete(parentAlias); }
    }

    [MacOnlyFact]
    public async Task A_loaded_list_with_contradictory_native_aliases_is_refused_without_rewriting_it()
    {
        var path = Path.Combine(_root, "MixedCase.txt");
        File.WriteAllText(path, "x");
        var alias = Path.Combine(_root, "parent-alias");
        Directory.CreateSymbolicLink(alias, _root);
        try
        {
            var store = new PathListStore();
            store.Save([new() { Path = path, DesiredVisibility = DesiredVisibility.Hidden },
                new() { Path = Path.Combine(alias, "MixedCase.txt"), DesiredVisibility = DesiredVisibility.Shown }]);
            var file = Path.Combine(_root, PathListStore.FileName);
            var before = File.ReadAllBytes(file);
            var loaded = store.Load().Value;
            var vm = new MainWindowViewModel(new VisibilityCalls(new MacVisibilityService()), store,
                new FakeSettingsStore(), new AppSettings(), new FakeJsonStore<AppState>(), new AppState(), loaded);
            await Assert.ThrowsAsync<UnreadableStoreException>(() => vm.ValidateLoadedPathsAsync());
            Assert.Equal(before, File.ReadAllBytes(file));
        }
        finally { Directory.Delete(alias); }
    }

    [MacOnlyFact]
    public void Entry_identity_preserves_a_final_link_and_a_dangling_link()
    {
        var target = Path.Combine(_root, "target");
        File.WriteAllText(target, "x");
        var link = Path.Combine(_root, "Link");
        var broken = Path.Combine(_root, "Broken");
        File.CreateSymbolicLink(link, target);
        File.CreateSymbolicLink(broken, Path.Combine(_root, "absent"));
        var service = new MacVisibilityService();
        Assert.Equal("Link", Path.GetFileName(service.ResolvePath(link)));
        Assert.Equal("Broken", Path.GetFileName(service.ResolvePath(broken)));
        Assert.NotEqual(service.ResolvePath(target), service.ResolvePath(link));
        if (MacFs.TryGetFlags(link.ToLowerInvariant(), false, out _))
            Assert.Equal(service.ResolvePath(link), service.ResolvePath(link.ToLowerInvariant()));
        if (MacFs.TryGetFlags(broken.ToLowerInvariant(), false, out _))
            Assert.Equal(service.ResolvePath(broken), service.ResolvePath(broken.ToLowerInvariant()));
    }

    [MacOnlyFact]
    public async Task Show_after_parent_alias_retarget_and_reload_restores_the_original_link_only()
    {
        var original = Directory.CreateDirectory(Path.Combine(_root, "original")).FullName;
        var replacement = Directory.CreateDirectory(Path.Combine(_root, "replacement")).FullName;
        var target = Path.Combine(_root, "target");
        File.WriteAllText(target, "target sentinel");
        var originalLink = Path.Combine(original, "Link");
        var replacementLink = Path.Combine(replacement, "Link");
        File.CreateSymbolicLink(originalLink, target);
        File.CreateSymbolicLink(replacementLink, target);
        var alias = Path.Combine(_root, "parent-alias");
        Directory.CreateSymbolicLink(alias, original);
        var service = new MacVisibilityService();
        var store = new PathListStore();
        var vm = new MainWindowViewModel(new VisibilityCalls(service), store,
            new FakeSettingsStore(), new AppSettings(), new FakeJsonStore<AppState>(), new AppState(), []);
        try
        {
            await vm.AddPathsCommand.ExecuteAsync([Path.Combine(alias, "Link")]);
            var stored = Assert.Single(store.Load().Value).Path;
            Assert.Equal(service.ResolvePath(originalLink), stored);
            Assert.Equal(ActualState.Hidden, service.Inspect(originalLink).ActualState);
            Assert.Equal(ActualState.Visible, service.Inspect(target).ActualState);
            await vm.AddPathsCommand.ExecuteAsync([originalLink]);
            Assert.Single(vm.Rows);

            Directory.Delete(alias);
            Directory.CreateSymbolicLink(alias, replacement);
            // Keep the replacement hidden: accidentally showing through the alias must fail this test.
            service.Hide(replacementLink, SystemBit.Keep);
            await vm.ReloadCommand.ExecuteAsync(null);
            await vm.ShowAllCommand.ExecuteAsync(null);

            Assert.Equal(stored, Assert.Single(store.Load().Value).Path);
            Assert.Equal(ActualState.Visible, service.Inspect(originalLink).ActualState);
            Assert.Equal(ActualState.Hidden, service.Inspect(replacementLink).ActualState);
            Assert.Equal(ActualState.Visible, service.Inspect(target).ActualState);
        }
        finally
        {
            await vm.ScanTask;
            Directory.Delete(alias);
        }
    }

    private static void DiskImage(params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/hdiutil") { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Disposable disk-image fixture did not settle.");
        }
        Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
    }

    [MacOnlyFact]
    public async Task Distinct_case_only_files_on_a_case_sensitive_volume_can_both_be_managed()
    {
        var image = Path.Combine(_root, "fixture.sparseimage");
        var mount = Directory.CreateDirectory(Path.Combine(_root, "mount")).FullName;
        DiskImage("create", "-size", "64m", "-type", "SPARSE", "-fs", "Case-sensitive APFS", "-volname", "PathHideFixture", image);
        DiskImage("attach", "-nobrowse", "-noautoopen", "-mountpoint", mount, image);
        try
        {
            var upper = Path.Combine(mount, "Foo");
            var lower = Path.Combine(mount, "foo");
            File.WriteAllText(upper, "upper sentinel");
            File.WriteAllText(lower, "lower sentinel");
            Assert.Equal("upper sentinel", File.ReadAllText(upper));
            Assert.Equal("lower sentinel", File.ReadAllText(lower));
            var paths = new FakeJsonStore<List<PathEntry>>();
            var vm = Model(paths);
            await vm.AddPathsCommand.ExecuteAsync([upper, lower]);
            Assert.Equal(2, vm.Rows.Count);
            var store = new PathListStore();
            store.Save(paths.Value);
            Assert.Equal(2, store.Load().Value.Count);
            Assert.Equal(ActualState.Hidden, new MacVisibilityService().Inspect(upper).ActualState);
            Assert.Equal(ActualState.Hidden, new MacVisibilityService().Inspect(lower).ActualState);

            var upperRoot = Directory.CreateDirectory(Path.Combine(mount, "Root")).FullName;
            var lowerRoot = Directory.CreateDirectory(Path.Combine(mount, "root")).FullName;
            Assert.NotEqual(SingleInstanceLease.MutexName(upperRoot), SingleInstanceLease.MutexName(lowerRoot));
        }
        finally { DiskImage("detach", mount); }
    }
}
