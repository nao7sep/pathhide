using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PathHide.I18n;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.I18n;

/// <summary>
/// The rendered-key gate (localization conventions): no catalogue key ever reaches the screen.
///
/// A key is a string like any other, so neither the compiler nor the source scan notices one handed
/// to a control without the translator — a whole group heading showed as <c>tasks.overdue</c> in
/// another app before this check existed. These open each surface and read what is actually drawn.
/// </summary>
public class RenderedKeyTests : WindowTest
{
    [AvaloniaFact]
    public async Task the_main_window_shows_no_key()
    {
        var (window, viewModel) = PopulatedMainWindow.Create();
        Show(window);
        await PopulatedMainWindow.SettleAsync(viewModel);
        Dispatcher.UIThread.RunJobs();

        // The grid, the status bar and both result strips all hold words from the catalogue.
        Assert.NotEmpty(window.GetVisualDescendants().OfType<DataGridCell>());
        Assert.NotEmpty(viewModel.OperationalResults);
        Assert.NotNull(viewModel.PathAddResult);
        AssertNoKeys(window);

        // The menu's items are not on screen until it opens, so the walk above cannot see them.
        var keys = Keys();
        foreach (var name in new[] { "RecordsMenuItem", "SettingsMenuItem", "ShortcutsMenuItem", "AboutMenuItem" })
        {
            var header = Assert.IsType<string>(window.FindControl<MenuItem>(name)!.Header);
            Assert.DoesNotContain(header, keys);
        }
    }

    [AvaloniaFact]
    public void the_main_windows_empty_list_shows_no_key()
    {
        var window = Show(PopulatedMainWindow.Empty());

        AssertNoKeys(window);
    }

    [AvaloniaFact]
    public void the_records_window_shows_no_key()
    {
        // Rows at every level, a launch named as this one, a selected record with fields and an error,
        // and the note a failed next page leaves at the end of the list.
        var reader = new FakeRecordsReader { Sessions = ["2026-10-04T08:00:00.000Z"] };
        reader.Details[3] = new global::PathHide.Storage.RecordDetail(
            3, "2026-10-04T08:00:00.000Z", "2026-10-04T09:00:03.000Z", "error", "save failed", "{\"a\":1}", "{\"type\":\"IOException\"}");
        var records = new RecordsViewModel(reader, "2026-10-04T08:00:00.000Z", _ => Task.CompletedTask);
        var settings = new FakeSettingsStore();
        var owner = new MainWindowViewModel(
            new BoundedVisibility(new FakeVisibilityService()), new FakeJsonStore<List<PathEntry>>(), settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState());
        var window = Show(new RecordsWindow(records, owner));
        reader.LastPage.Answer(false);
        Dispatcher.UIThread.RunJobs();
        records.SelectedLaunch = records.LaunchOptions[1];
        Dispatcher.UIThread.RunJobs();
        reader.LastPage.Answer(true,
            new(3, "2026-10-04T08:00:00.000Z", "2026-10-04T09:00:03.000Z", "error", "save failed"),
            new(2, "2026-10-04T08:00:00.000Z", "2026-10-04T09:00:02.000Z", "warn", "slow"),
            new(1, "2026-10-04T08:00:00.000Z", "2026-10-04T09:00:01.000Z", "debug", "probe"));
        Dispatcher.UIThread.RunJobs();
        records.SelectedRow = records.Rows[0];
        records.LoadMore();
        Dispatcher.UIThread.RunJobs();
        reader.LastPage.Fail();
        Dispatcher.UIThread.RunJobs();

        Assert.True(records.HasDetail);
        Assert.True(records.Detail!.HasFields && records.Detail.HasError);
        Assert.True(records.HasMoreNote);
        AssertNoKeys(window);
    }

    [AvaloniaFact]
    public void the_records_windows_notes_show_no_key()
    {
        var reader = new FakeRecordsReader();
        var records = new RecordsViewModel(reader, "s", _ => Task.CompletedTask);
        var settings = new FakeSettingsStore();
        var owner = new MainWindowViewModel(
            new BoundedVisibility(new FakeVisibilityService()), new FakeJsonStore<List<PathEntry>>(), settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState());
        var window = Show(new RecordsWindow(records, owner));

        AssertNoKeys(window);
        reader.LastPage.Fail();
        Dispatcher.UIThread.RunJobs();
        Assert.True(records.HasListNote);
        AssertNoKeys(window);
    }

    [AvaloniaFact]
    public void the_about_dialog_shows_no_key()
    {
        var dialog = Show(new AboutDialog(_ => false));

        AssertNoKeys(dialog);
    }

    [AvaloniaFact]
    public void the_shortcuts_dialog_shows_no_key()
    {
        var window = Show(new Window());
        var dialog = Show(new ShortcutsDialog(ShortcutCatalog.Build(window)));

        AssertNoKeys(dialog);
    }

    [AvaloniaFact]
    public void the_settings_dialog_shows_no_key()
    {
        // With the Windows-only section on, and a failed save's sentence on screen.
        var dialog = Show(new SettingsDialog(
            Languages.System, string.Empty, ThemePreference.System,
            isHiddenAndSystem: false, showWindowsHideMode: true,
            (_, _, _, _) => Task.FromResult<Message?>(Message.Of("failure.settingsSave"))));

        AssertNoKeys(dialog);
    }

    [AvaloniaFact]
    public void the_notices_and_confirmations_show_no_key()
    {
        AssertNoKeys(Show(NoticeDialog.CreateStartupFailure(
            Message.Of("startup.failedTitle"), Message.Of("failure.startupStorage"))));
        AssertNoKeys(Show(NoticeDialog.CreateStartupFailure(
            Message.Of("startup.pathListTitle"),
            global::PathHide.ViewModels.FailurePresentation.PathListStartup(new global::PathHide.Storage.UnreadableStoreException(
                "paths", "/r/paths.json", new System.UnauthorizedAccessException())))));
    }

    private static void AssertNoKeys(Visual root)
    {
        var keys = Keys();
        var found = new List<string>();

        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            if (text.Text is { } drawn && keys.Contains(drawn.Trim()))
                found.Add($"text: {drawn}");
        }

        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            if (control is ContentControl { Content: string content } && keys.Contains(content.Trim()))
                found.Add($"content: {content}");
            if (control is Avalonia.Controls.Primitives.HeaderedContentControl { Header: string header } && keys.Contains(header.Trim()))
                found.Add($"header: {header}");
            if (AutomationProperties.GetName(control) is { } name && keys.Contains(name.Trim()))
                found.Add($"automation name: {name}");
            if (AutomationProperties.GetHelpText(control) is { } help && keys.Contains(help.Trim()))
                found.Add($"help text: {help}");
            if (ToolTip.GetTip(control) is string tip && keys.Contains(tip.Trim()))
                found.Add($"tooltip: {tip}");
        }

        if (root is Window window && window.Title is { } title && keys.Contains(title.Trim()))
            found.Add($"title: {title}");

        Assert.Empty(found);
    }

    private static HashSet<string> Keys()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(CatalogueTests.LocalesDirectory(), "en.json")));
        return document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(System.StringComparer.Ordinal);
    }
}
