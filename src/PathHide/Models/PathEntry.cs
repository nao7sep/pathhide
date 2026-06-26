namespace PathHide.Models;

public sealed class PathEntry
{
    public required string Path { get; set; }
    public required DesiredVisibility DesiredVisibility { get; set; }
}
