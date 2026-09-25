using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using PathHide.Views;
using PathHide.Tests.I18n;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class ViewActionBoundaryTests
{
    [AvaloniaFact]
    public async Task Shortcut_window_action_failure_is_owned_and_safe()
    {
        var settings = new FakeJsonStore<AppSettings>();
        var vm = new MainWindowViewModel(
            new BoundedVisibility(new FakeVisibilityService()),
            new FakeJsonStore<List<PathEntry>>(),
            settings,
            settings.Load().Value);
        var window = new MainWindow { DataContext = vm };
        var hostile = new IOException("EACCES IPC /private/tmp/PATHHIDE-SHORTCUT-SENTINEL");

        await window.OwnViewActionAsync(() => Task.FromException(hostile));

        var result = Assert.Single(vm.OperationalResults);
        Assert.Equal(OperationalResultOwner.Window, result.Owner);
        Assert.Contains("window action", English.Of(result.Message));
        Assert.DoesNotContain("EACCES", English.Of(result.Message));
        Assert.DoesNotContain("PATHHIDE-SHORTCUT-SENTINEL", English.Of(result.Message));
    }
}
