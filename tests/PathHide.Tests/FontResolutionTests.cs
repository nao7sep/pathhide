using Avalonia.Headless.XUnit;
using Avalonia.Media;
using PathHide;
using PathHide.Models;
using Xunit;

namespace PathHide.Tests;

/// <summary>
/// The bundled Inter must actually load. A bare "Inter" family name does not reach the
/// embedded collection `.WithInterFont()` registers — with no system Inter installed it
/// silently falls back to the platform font (Helvetica on macOS), whose tight
/// ascent-over-cap makes every label in the app sit visibly high. Found in the wild as
/// "text is not vertically centered"; the collection URI is what fixes it, and this
/// guards that the URI keeps resolving to real Inter.
/// </summary>
public sealed class FontResolutionTests
{
    [AvaloniaFact]
    public void BundledFontUriResolvesToInter()
    {
        var typeface = new Typeface(new FontFamily(AppSettings.BundledUiFontUri));
        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface));
        Assert.StartsWith("Inter", glyphTypeface.FamilyName);
    }

    [AvaloniaFact]
    public void ResolverFallbackResolvesToInter()
    {
        var typeface = new Typeface(UiFont.Resolve("No Such Font 99999"));
        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface));
        Assert.StartsWith("Inter", glyphTypeface.FamilyName);
    }
}
