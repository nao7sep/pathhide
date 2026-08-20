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

    [Fact]
    public void UiFontFamilyValue_FlattensAPastedLineBreak()
    {
        // A single-line control does not keep its value single-line - a paste
        // carries whatever it carried. Trimming the ends only left an interior
        // break to be persisted verbatim, match no installed family, and come
        // back as a multi-line value in the settings box.
        Assert.Equal("Hiragino Sans, Inter", UiFontFamilyValue.Normalize("Hiragino Sans,\nInter"));
        Assert.Equal("Hiragino Sans", UiFontFamilyValue.Normalize("  Hiragino Sans\r\n"));
        Assert.Equal("A B", UiFontFamilyValue.Normalize("A\t\tB"));
    }

    [Fact]
    public void UiFontFamilyValue_LeavesAnOrdinaryValueAlone()
    {
        Assert.Equal("Inter", UiFontFamilyValue.Normalize("Inter"));
        Assert.Equal("Hiragino Sans, Inter", UiFontFamilyValue.Normalize("Hiragino Sans, Inter"));
        Assert.Equal(string.Empty, UiFontFamilyValue.Normalize(null));
        Assert.Equal(string.Empty, UiFontFamilyValue.Normalize("   "));
    }
}
