using System;
using PathHide.I18n;
using Xunit;

namespace PathHide.Tests.I18n;

/// <summary>
/// A message that sends the reader to the app's records, shown while the app runs, names the Records
/// item and the menu it is in, as that language's menu shows them. A startup failure, where Records
/// cannot open, points to the session log instead and never to the menu.
/// </summary>
public class RecordsPointerTests
{
    private static readonly string[] PointsToRecords =
    [
        "about.openGitHubFailed",
        "about.openIssuesFailed",
        "quarantine.settingsBody",
    ];

    private static string Text(Catalogue catalogue, string key)
    {
        Assert.True(catalogue.TryGet(key, out var entry), $"{catalogue.Tag} has no {key}.");
        return Assert.IsType<string>(entry.Text);
    }

    public static TheoryData<string> Tags() => new(Languages.Tags);

    [Theory]
    [MemberData(nameof(Tags))]
    public void Messages_shown_while_the_app_runs_point_to_records_in_the_menu(string tag)
    {
        var catalogue = Catalogue.For(tag);
        var records = Text(catalogue, "menu.records");
        var menu = Text(catalogue, "menu.button");

        foreach (var key in PointsToRecords)
        {
            var text = Text(catalogue, key);
            Assert.True(text.Contains(records, StringComparison.Ordinal), $"{tag} {key} does not name {records}: {text}");
            Assert.True(text.Contains(menu, StringComparison.OrdinalIgnoreCase), $"{tag} {key} does not name the menu: {text}");
        }
    }

    [Theory]
    [MemberData(nameof(Tags))]
    public void The_startup_failure_does_not_send_the_reader_to_the_menu(string tag)
    {
        var catalogue = Catalogue.For(tag);
        var text = Text(catalogue, "failure.pathListStartup");

        Assert.False(text.Contains(Text(catalogue, "menu.button"), StringComparison.OrdinalIgnoreCase), $"{tag}: {text}");
    }
}
