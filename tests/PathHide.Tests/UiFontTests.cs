using System.Linq;
using System.Text.Json;
using PathHide;
using PathHide.Models;
using Xunit;

namespace PathHide.Tests;

/// <summary>
/// The UI-font setting and its resolver. ParseFamilies is the parsing edge of the resolver (the
/// FontManager-backed "first installed wins, else Inter" path mirrors daynote's tested resolver and
/// needs a headless app this project does not host).
/// </summary>
public sealed class UiFontTests
{
    [Fact]
    public void ParseFamilies_splits_trims_strips_quotes_and_drops_empties()
    {
        Assert.Equal(
            new[] { "Helvetica Neue", "Segoe UI", "Roboto" },
            UiFont.ParseFamilies("\"Helvetica Neue\", Segoe UI , , 'Roboto'").ToArray());
    }

    [Fact]
    public void ParseFamilies_yields_nothing_for_blank_values()
    {
        Assert.Empty(UiFont.ParseFamilies(null));
        Assert.Empty(UiFont.ParseFamilies(string.Empty));
        Assert.Empty(UiFont.ParseFamilies("   "));
    }

    [Fact]
    public void Default_ui_font_is_the_bundled_inter()
    {
        Assert.Equal("Inter", new AppSettings().UiFontFamily);
        Assert.Equal("Inter", AppSettings.DefaultUiFontFamily);
    }

    [Fact]
    public void App_settings_json_round_trips_the_ui_font()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var settings = new AppSettings { UiFontFamily = "Iosevka, monospace" };
        var json = JsonSerializer.Serialize(settings, options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, options)!;
        Assert.Equal("Iosevka, monospace", restored.UiFontFamily);
    }
}
