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
    /// Hides <paramref name="path"/>, doing <paramref name="system"/> to its Windows System attribute,
    /// and returns whether this write turned System on. macOS has one hidden flag, ignores
    /// <paramref name="system"/> and returns false.
    /// </summary>
    bool Hide(string path, SystemBit system);

    /// <summary>Shows <paramref name="path"/>, clearing or keeping its Windows System attribute.</summary>
    void Show(string path, SystemBit system);

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
