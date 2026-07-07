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
}
