namespace PathHide.Models;

public sealed class AppSettings
{
    /// <summary>The bundled default UI (chrome) font, registered via <c>.WithInterFont()</c>.</summary>
    public const string DefaultUiFontFamily = "Inter";

    // App appearance — the UI (chrome) font family. Family only; an empty value falls back to the
    // bundled default (Inter). Applied app-wide.
    public string UiFontFamily { get; set; } = DefaultUiFontFamily;

    public WindowsHideMode WindowsHideMode { get; set; } = WindowsHideMode.HiddenOnly;
}
