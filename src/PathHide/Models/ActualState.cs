namespace PathHide.Models;

public enum ActualState
{
    Hidden,
    Visible,
    Missing,
    AccessDenied,
    Error,
    Unknown,

    /// <summary>The path did not answer in time (a stalled network share or a half-ejected volume).</summary>
    Unresponsive,
}
