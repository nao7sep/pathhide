using System;
using PathHide.Services;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using PathHide.Models;

namespace PathHide;

/// <summary>
/// Resolves the user's UI (chrome) font-family string to a concrete <see cref="FontFamily"/>.
/// Per the app-chrome-conventions' native-toolkit rule, the free-text value may be a comma-separated
/// list; this picks the first family actually installed and otherwise falls back to the bundled
/// default (Inter), so a misspelled or absent family never leaves the chrome unstyled.
/// </summary>
public static class UiFont
{
    /// <summary>Splits a comma-separated family string into trimmed, unquoted names.</summary>
    public static IEnumerable<string> ParseFamilies(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var part in value.Split(','))
        {
            var name = part.Trim().Trim('"', '\'').Trim();
            if (name.Length > 0)
            {
                yield return name;
            }
        }
    }

    /// <summary>
    /// The first requested family that is actually installed, or the bundled default when none match.
    /// </summary>
    public static FontFamily Resolve(string? value)
    {
        foreach (var name in ParseFamilies(value))
        {
            if (IsInstalled(name))
            {
                return new FontFamily(name);
            }
        }

        return new FontFamily(AppSettings.BundledUiFontUri);
    }

    private static bool IsInstalled(string name)
    {
        try
        {
            return FontManager.Current.SystemFonts.Any(
                family => string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            // A font-manager hiccup must never crash settings; treat the family as absent so the
            // caller falls back to the bundled default. Logged, not swallowed: without a line here
            // a user whose font never applies leaves a log reading "ui font changed" followed by
            // silence, and the reason is unrecoverable afterwards.
            Log.Warn("ui font: could not enumerate system fonts", ex, new { name });
            return false;
        }
    }
}
