namespace PathHide.Models;

public sealed class PathEntry
{
    public required string Path { get; set; }
    public required DesiredVisibility DesiredVisibility { get; set; }

    /// <summary>
    /// Whether PathHide set the item's Windows System attribute itself, and so may clear it again; a
    /// System attribute the item already had is left alone. Absent (null) means ownership is unknown and does not permit clearing System.
    /// </summary>
    public bool? SystemSetByPathHide { get; set; }
}
