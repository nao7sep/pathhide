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
    /// <summary>
    /// Hides <paramref name="path"/>. <paramref name="mode"/> decides whether Windows also sets System;
    /// macOS has one hidden flag and ignores it. A batch passes the mode it started with, so a Settings
    /// change while it runs applies to the next command, not to the rest of this one.
    /// </summary>
    void Hide(string path, WindowsHideMode mode);
    void Show(string path);

    /// <summary>
    /// The entry's canonical spelling, resolving parent aliases and actual leaf case without
    /// following a final symbolic link, or null when unavailable. Implementations must not throw.
    /// </summary>
    string? ResolvePath(string path);

    /// <summary>
    /// Whether <see cref="ResolvePath"/> can answer anything but null on this system; when it cannot,
    /// nothing is run to ask it.
    /// </summary>
    bool ResolvesPaths { get; }
}
