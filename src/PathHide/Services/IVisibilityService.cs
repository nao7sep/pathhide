using PathHide.Models;

namespace PathHide.Services;

public interface IVisibilityService
{
    /// <summary>
    /// Describes the path's current state. Implementations must not throw: failures are
    /// reported as <see cref="ActualState.Error"/> or <see cref="ActualState.AccessDenied"/>,
    /// so callers (including the re-inspect after a failed Hide/Show) can rely on it.
    /// </summary>
    PathInspection Inspect(string path);
    void Hide(string path);
    void Show(string path);

    /// <summary>
    /// The spelling of an existing <paramref name="directory"/> with every alias in it resolved, or
    /// null when the platform has none to resolve or the directory cannot be resolved. Decides a
    /// new entry's identity once, when it is added (see <see cref="PathNormalizer.Rebase"/>).
    /// Implementations must not throw.
    /// </summary>
    string? ResolveDirectory(string directory);
}
