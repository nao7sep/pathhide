using System;

namespace PathHide.Storage;

/// <summary>
/// A store records a format version newer than this build reads. It is intact data, not a corrupt file:
/// it is reported by name and left exactly as it is, so the version that wrote it can still read it
/// (store-recovery-conventions).
/// </summary>
public sealed class NewerFormatException(string path, int version, int supported)
    : Exception($"{path} records format version {version}; this build reads up to {supported}.")
{
    public string Path { get; } = path;

    public int Version { get; } = version;

    public int Supported { get; } = supported;
}
