namespace PathHide.Models;

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
