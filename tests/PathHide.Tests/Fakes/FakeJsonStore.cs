using System.IO;
using PathHide.Storage;

namespace PathHide.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IJsonStore{T}"/>. <see cref="ThrowOnSave"/> simulates a
/// persistence failure so the view model's save-failure rollback can be tested
/// without making a real file unwritable.
/// </summary>
public sealed class FakeJsonStore<T> : IJsonStore<T> where T : class, new()
{
    public T Value { get; set; } = new();
    public bool ThrowOnSave { get; set; }
    public int SaveCount { get; private set; }
    public int LoadCount { get; private set; }
    public T? LastSaved { get; private set; }

    /// <summary>Whether a live file "exists" — set by any successful <see cref="Save"/>, so
    /// <see cref="CreateIfMissing"/> models the real store's absence-only trigger.</summary>
    public bool FileExists { get; set; }

    public T Load()
    {
        LoadCount++;
        return Value;
    }

    public void Save(T value)
    {
        if (ThrowOnSave)
            throw new IOException("save failed (test)");

        SaveCount++;
        LastSaved = value;
        Value = value;
        FileExists = true;
    }

    public bool CreateIfMissing(T value)
    {
        if (FileExists)
            return false;

        Save(value);
        return true;
    }
}
