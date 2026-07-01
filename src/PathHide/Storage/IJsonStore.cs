namespace PathHide.Storage;

/// <summary>
/// Load/save contract for a JSON-backed document. Exists so callers (notably
/// <see cref="PathHide.ViewModels.MainWindowViewModel"/>) can depend on the
/// persistence behaviour without binding to <see cref="JsonStore{T}"/>'s file
/// I/O, which keeps that orchestration unit-testable with in-memory fakes.
/// </summary>
public interface IJsonStore<T> where T : class, new()
{
    T Load();
    void Save(T value);

    /// <summary>
    /// Writes <paramref name="value"/> only when the live file does not yet exist, so a built-in
    /// defaultable file (config.json) is present on disk after the first run rather than only after the
    /// first save. The single trigger is absence: an existing file is never inspected or overwritten.
    /// Returns true when a file was created. See the storage-path conventions' "Materializing settings
    /// on first run".
    /// </summary>
    bool CreateIfMissing(T value);
}
