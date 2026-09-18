using Avalonia;
using Avalonia.Styling;
using PathHide.Models;

namespace PathHide;

/// <summary>
/// The application's requested theme variant is PathHide's one theme authority (app-chrome
/// conventions, Theme): every window inherits it, Avalonia paints each native title bar from it,
/// and App.axaml's Light and Dark theme dictionaries resolve through it.
/// </summary>
public static class AppTheme
{
    /// <summary>System follows the OS through <see cref="ThemeVariant.Default"/>.</summary>
    public static ThemeVariant VariantFor(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    public static void Apply(ThemePreference preference)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = VariantFor(preference);
    }
}
