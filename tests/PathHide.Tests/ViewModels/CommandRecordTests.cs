using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using Xunit;
using static PathHide.Tests.ViewModels.MainWindowViewModelTests;

namespace PathHide.Tests.ViewModels;

/// <summary>
/// The records name what a command did to which paths: files a removed entry left hidden, or the paths
/// a Hide or Show changed, cannot be traced afterwards from counts alone.
/// </summary>
[Collection(ProcessLogCollection.Name)]
public sealed class CommandRecordTests
{
    private static string[] Paths(LogEntry entry, string field) =>
        [.. entry.Fields![field]!.AsArray().Select(path => path!.GetValue<string>())];

    [Fact]
    public async Task Hide_and_Show_record_which_paths_each_was_to_change()
    {
        using var log = new StartupDiagnosticLog("apply: start");
        var visibility = new FakeVisibilityService();
        var vm = CreateViewModel(visibility, new FakeJsonStore<List<PathEntry>>
        {
            Value = [Entry("/records/a"), Entry("/records/b")],
        });
        await vm.ScanTask;
        vm.Rows.Single(row => row.Path == "/records/b").IsSelected = true;

        await vm.ShowSelectedCommand.ExecuteAsync(null);
        await vm.HideAllCommand.ExecuteAsync(null);

        var shown = await log.Find(entry => entry.Message == "apply: start" && Paths(entry, "show").Length > 0)
            .WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["/records/b"], Paths(shown, "show"));
        Assert.Empty(Paths(shown, "hide"));
        var hidden = await log.Find(entry => entry.Message == "apply: start" && Paths(entry, "hide").Length == 2)
            .WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["/records/a", "/records/b"], Paths(hidden, "hide"));
    }

    [Fact]
    public async Task Remove_records_the_paths_it_stopped_tracking()
    {
        using var log = new StartupDiagnosticLog("remove paths: saved");
        var vm = CreateViewModel(new FakeVisibilityService(), new FakeJsonStore<List<PathEntry>>
        {
            Value = [Entry("/records/a"), Entry("/records/b")],
        });
        await vm.ScanTask;
        vm.Rows.Single(row => row.Path == "/records/a").IsSelected = true;

        await vm.RemoveSelectedCommand.ExecuteAsync(null);

        var removed = await log.Reported.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["/records/a"], Paths(removed, "paths"));
    }
}
