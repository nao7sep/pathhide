using System;

namespace PathHide.Storage;

/// <summary>
/// A store is present but cannot be read, and is left exactly as it is at its path: the path list
/// because it is the user's work product, any other store because setting it aside failed. The app
/// neither continues without it nor writes over it (store-recovery-conventions).
/// </summary>
public sealed class UnreadableStoreException(string label, string path, Exception reason)
    : Exception($"{path} could not be read.", reason)
{
    /// <summary>The label the store was created with, from <see cref="QuarantineJournal"/>.</summary>
    public string Label { get; } = label;

    public string Path { get; } = path;
}
