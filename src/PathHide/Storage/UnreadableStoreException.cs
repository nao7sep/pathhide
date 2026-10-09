using System;
using System.IO;

namespace PathHide.Storage;

/// <summary>
/// The path list is present but cannot be read, and is left exactly as it is at its path, because it is
/// the user's work product: the app neither continues without it nor writes over it
/// (store-recovery-conventions).
/// </summary>
public sealed class UnreadableStoreException(string label, string path, Exception reason)
    : Exception($"{path} could not be read.", reason)
{
    /// <summary>The label the store was created with, for the log.</summary>
    public string Label { get; } = label;

    public string Path { get; } = path;

    /// <summary>
    /// True when the file could not be opened or read at all, so the user should check access rather
    /// than repair its content.
    /// </summary>
    public bool IsAccessFailure { get; } = reason is IOException or UnauthorizedAccessException;
}
