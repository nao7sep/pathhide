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
}
