namespace PathHide.Models;

/// <summary>
/// How the app was last presented: state recorded from the user's direct handling of the windows,
/// kept apart from the settings they author (<see cref="AppSettings"/>), so a settings change or
/// reset never touches it and it never travels with the settings to another machine. Written only
/// once there is something to record, never on first run.
/// </summary>
public sealed record AppState
{
    /// <summary>The file this state lives in, under the storage root.</summary>
    public const string FileName = "state.json";

    // Last normal main-window geometry. Nullable primitives distinguish an absent
    // placement from coordinates at the origin; negative positions are valid on
    // displays left of or above the primary display.
    public int? WindowPositionX { get; init; }
    public int? WindowPositionY { get; init; }
    public double? WindowWidth { get; init; }
    public double? WindowHeight { get; init; }
    public bool WindowMaximized { get; init; }

    // The records window's own placement, kept the same way.
    public int? RecordsWindowPositionX { get; init; }
    public int? RecordsWindowPositionY { get; init; }
    public double? RecordsWindowWidth { get; init; }
    public double? RecordsWindowHeight { get; init; }
    public bool RecordsWindowMaximized { get; init; }

    // The width last dragged for the records window's list pane: the intent, never a clamp.
    public double? RecordsListWidth { get; init; }
}
