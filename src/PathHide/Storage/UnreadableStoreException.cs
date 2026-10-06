using System;

namespace PathHide.Storage;

/// <summary>
/// A store holding the user's work product is present but cannot be read. It is left exactly as it is
/// at its path: the app neither continues without it nor writes over it (store-recovery-conventions).
/// </summary>
public sealed class UnreadableStoreException(string path, Exception reason)
    : Exception($"{path} could not be read.", reason)
{
    public string Path { get; } = path;
}
