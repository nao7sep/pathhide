namespace PathHide.Storage;

/// <summary>
/// The outcome of a load: the value, and whether a file was present but could not be used, so the
/// value is the defaults while the file stays untouched.
/// </summary>
/// <remarks>
/// An absent file and an unusable one both yield defaults, but they mean opposite things: absent is
/// first run, unusable means there was content that could not be read. Only stores holding harmless
/// preferences or presentation state reach this outcome; a store holding the user's work product
/// throws instead (see <see cref="JsonStore{T}"/>).
/// </remarks>
public readonly record struct LoadedStore<T>(T Value, bool WasUnreadable);

/// <summary>
/// Load/save contract for a JSON-backed document. Exists so callers (notably
/// <see cref="PathHide.ViewModels.MainWindowViewModel"/>) can depend on the
/// persistence behaviour without binding to <see cref="JsonStore{T}"/>'s file
/// I/O, which keeps that orchestration unit-testable with in-memory fakes.
/// </summary>
public interface IJsonStore<T> where T : class, new()
{
    LoadedStore<T> Load();
    void Save(T value);
}
