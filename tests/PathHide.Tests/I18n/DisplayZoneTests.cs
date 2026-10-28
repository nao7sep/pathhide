using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PathHide.I18n;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using PathHide.Views;
using Xunit;
using static PathHide.Tests.ViewModels.RecordFormatTests;

namespace PathHide.Tests.I18n;

/// <summary>
/// The zone the app shows times in is process-wide, so tests that change it run alone: anything else
/// formatting a time meanwhile would see the wrong zone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DisplayZoneCollection
{
    public const string Name = "Display zone";
}

/// <summary>The display zone (timestamp-conventions, Time zones): System by default, or one listed IANA zone.</summary>
public sealed class DisplayZonePreferenceTests
{
    [Fact]
    public void The_list_is_the_platforms_IANA_zones_in_order_without_repeats()
    {
        var zones = DisplayZone.Zones;

        Assert.Contains("Asia/Tokyo", zones);
        Assert.Contains("America/New_York", zones);
        Assert.Equal(zones.Order(StringComparer.Ordinal), zones);
        Assert.Equal(zones.Count, zones.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(DisplayZone.System, zones);
    }

    [Theory]
    [InlineData("system", "system")]
    [InlineData("Asia/Tokyo", "Asia/Tokyo")]
    [InlineData("Mars/Olympus", null)]
    [InlineData("Tokyo Standard Time", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_System_or_a_listed_zone_is_a_preference(string? saved, string? expected) =>
        Assert.Equal(expected, DisplayZone.ParsePreference(saved));

    [Fact]
    public void System_is_the_computers_zone_and_a_chosen_zone_is_that_zone()
    {
        Assert.Same(TimeZoneInfo.Local, DisplayZone.Resolve(DisplayZone.System));
        Assert.Same(TimeZoneInfo.Local, DisplayZone.Resolve("Mars/Olympus"));
        var tokyo = DisplayZone.Resolve("Asia/Tokyo");
        Assert.Equal(TimeSpan.FromHours(9), tokyo.GetUtcOffset(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void System_is_named_by_a_canonical_IANA_zone()
    {
        var name = DisplayZone.SystemZoneName();

        Assert.False(string.IsNullOrWhiteSpace(name));
        if (!OperatingSystem.IsMacOS())
            Assert.Equal(DisplayZone.IanaName(TimeZoneInfo.Local) ?? TimeZoneInfo.Local.Id, name);
    }

    // macOS answers with the canonical name its own time-zone API gives, never a legacy alias .NET
    // may report for the same data.
    [MacOnlyFact]
    public void On_macOS_System_is_named_as_macOS_names_the_computers_zone()
    {
        var systemZone = PathHide.Views.ObjC.Send(PathHide.Views.ObjC.Class("NSTimeZone"), "systemTimeZone");

        Assert.Equal(PathHide.Views.ObjC.String(PathHide.Views.ObjC.Send(systemZone, "name")), DisplayZone.SystemZoneName());
    }

    [Theory]
    [InlineData("/var/db/timezone/zoneinfo/Asia/Tokyo", "Asia/Tokyo")]
    [InlineData("/usr/share/zoneinfo/America/Argentina/Buenos_Aires", "America/Argentina/Buenos_Aires")]
    [InlineData("../usr/share/zoneinfo/UTC", "UTC")]
    [InlineData("/usr/share/zoneinfo/", null)]
    [InlineData("/etc/somewhere/else", null)]
    [InlineData(null, null)]
    public void The_zone_is_what_follows_zoneinfo_in_the_localtime_link(string? target, string? zone) =>
        Assert.Equal(zone, DisplayZone.ZoneFromLink(target));
}

[Collection(DisplayZoneCollection.Name)]
public sealed class DisplayZoneChangeTests : WindowTest
{
    private const string ThisLaunch = "2026-10-04T08:00:00.000Z";

    private static MainWindowViewModel Owner(FakeSettingsStore settings) =>
        new(new VisibilityCalls(new FakeVisibilityService()), new FakeJsonStore<List<PathEntry>>(), settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState(), []);

    [Fact]
    public async Task Saving_a_zone_stores_it_and_puts_it_in_effect()
    {
        var settings = new FakeSettingsStore();
        var vm = Owner(settings);
        try
        {
            Assert.Null(await vm.TryApplySettingsAsync("en", "", false, ThemePreference.System, "Asia/Tokyo"));

            Assert.Equal("Asia/Tokyo", settings.LastSaved!.DisplayTimeZone);
            Assert.Equal("Asia/Tokyo", vm.DisplayTimeZone);
            Assert.Equal("Asia/Tokyo", DisplayZone.Preference);
        }
        finally
        {
            DisplayZone.Use(DisplayZone.System);
        }
    }

    [AvaloniaFact]
    public void An_open_Records_window_redraws_its_times_in_a_newly_chosen_zone()
    {
        var reader = new FakeRecordsReader();
        var records = new RecordsViewModel(reader, ThisLaunch);
        var window = Show(new RecordsWindow(records, Owner(new FakeSettingsStore())));
        try
        {
            DisplayZone.Use("Europe/London");
            reader.Unanswered.Single().Answer(false, Record(1, session: ThisLaunch));
            Dispatcher.UIThread.RunJobs();
            var row = records.Rows.Single();
            var inLondon = row.TimeText;
            var redrawn = 0;
            row.PropertyChanged += (_, e) => redrawn += string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(row.TimeText) ? 1 : 0;

            DisplayZone.Use("Asia/Tokyo");

            Assert.True(redrawn > 0);
            Assert.NotEqual(inLondon, row.TimeText);
            Assert.Equal(
                RecordFormat.ListTime(row.Record.Time, Localizer.Current.Culture, DisplayZone.Resolve("Asia/Tokyo")),
                row.TimeText);
        }
        finally
        {
            DisplayZone.Use(DisplayZone.System);
            window.Close();
        }
    }
}
