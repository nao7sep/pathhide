namespace PathHide.Models;

/// <summary>
/// How the app was last presented: state recorded from the user's direct handling of the window,
/// kept apart from the settings they author (<see cref="AppSettings"/>), so a settings change or
/// reset never touches it and it never travels with the settings to another machine. Written only
/// once there is something to record, never on first run.
/// </summary>
public sealed class AppState
{
    /// <summary>The file this state lives in, under the storage root.</summary>
    public const string FileName = "state.json";

    // Last normal main-window geometry. Nullable primitives distinguish an absent
    // placement from coordinates at the origin; negative positions are valid on
    // displays left of or above the primary display.
    public int? WindowPositionX { get; set; }
    public int? WindowPositionY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
}
