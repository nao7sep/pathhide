using System;
namespace PathHide.Models;

public static class UiFontFamilyValue
{
    /// <summary>
    /// Normalizes a UI-font value to the single line the field is meant to hold: interior line
    /// breaks flattened to spaces, runs of whitespace collapsed, then trimmed.
    /// </summary>
    /// <remarks>
    /// A single-line control does not reliably keep its value single-line — a paste carries
    /// whatever it carried. Trimming the ends only meant an interior break survived, was persisted
    /// verbatim into config.json, matched no installed family, and came back as a multi-line value
    /// in the settings box. Applied on commit, which is where the text-cleanup conventions put it
    /// rather than assuming the control enforces it.
    /// </remarks>
    public static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public sealed class AppSettings
{
    /// <summary>The bundled default UI (chrome) font, registered via <c>.WithInterFont()</c>.</summary>
    public const string DefaultUiFontFamily = "Inter";

    /// <summary>
    /// The bundled Inter as the font manager reaches it. A bare "Inter" does NOT resolve
    /// to the embedded collection `.WithInterFont()` registers — with no system Inter
    /// installed it silently falls back to the platform default (Helvetica on macOS),
    /// whose ascent barely clears its cap height, so every label sits visibly high.
    /// The display name stays "Inter"; this URI is what actually loads it.
    /// </summary>
    public const string BundledUiFontUri = "fonts:Inter#Inter";

    // App appearance — the UI (chrome) font family. Family only; an empty value falls back to the
    // bundled default (Inter). Applied app-wide.
    public string UiFontFamily { get; set; } = DefaultUiFontFamily;

    public WindowsHideMode WindowsHideMode { get; set; } = WindowsHideMode.HiddenOnly;
}
