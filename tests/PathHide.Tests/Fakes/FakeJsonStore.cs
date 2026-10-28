using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Storage;

namespace PathHide.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IJsonStore{T}"/>. <see cref="ThrowOnSave"/> simulates a
/// persistence failure so the view model's save-failure rollback can be tested
/// without making a real file unwritable.
/// </summary>
public class FakeJsonStore<T> : IJsonStore<T> where T : class, new()
{
    public T Value { get; set; } = new();
    public bool ThrowOnSave { get; set; }

    /// <summary>When set, <see cref="Load"/> throws it, like a file a newer build wrote.</summary>
    public System.Exception? LoadException { get; set; }
    public int SaveCount { get; private set; }
    public int LoadCount { get; private set; }
    public T? LastSaved { get; private set; }

    /// <summary>When set, <see cref="Load"/> blocks on this gate first, like a read from a slow share.</summary>
    public ManualResetEventSlim? LoadGate { get; set; }

    /// <summary>Signals entry into a load held by <see cref="LoadGate"/>.</summary>
    public TaskCompletionSource LoadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LoadedStore<T> Load()
    {
        if (LoadGate is not null)
            LoadEntered.TrySetResult();
        LoadGate?.Wait();
        LoadCount++;
        if (LoadException is not null)
            throw LoadException;
        return new LoadedStore<T>(Value, WasUnreadable: false);
    }

    /// <summary>When set, <see cref="Save"/> blocks on this gate first, like a write to a slow share.</summary>
    public ManualResetEventSlim? SaveGate { get; set; }

    /// <summary>Signals entry into a save held by <see cref="SaveGate"/>.</summary>
    public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Released once after each save that landed.</summary>
    public SemaphoreSlim Saves { get; } = new(0);

    public void Save(T value)
    {
        if (SaveGate is not null)
            SaveEntered.TrySetResult();
        SaveGate?.Wait();

        if (ThrowOnSave)
            throw new IOException("save failed (test)");

        SaveCount++;
        LastSaved = value;
        Value = value;
        Saves.Release();
    }
}
