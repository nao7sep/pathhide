using System;
using System.IO;
using System.Text.Json;
using Serilog;

namespace PathHide.Storage;

/// <summary>
/// Generic JSON-backed store with atomic replace, sidecar backup, and
/// backup-fallback recovery on load. Two files are written: <c>{file}</c>
/// is the live document; <c>{file}.bak</c> holds the previous successful
/// version. If the live document is missing or unparseable on load, the
/// backup is tried before the type's default-constructed value is returned.
/// </summary>
/// <remarks>
/// Caller responsibilities: this store does not impose any ordering or
/// canonicalisation on the value it receives. If on-disk ordering matters
/// (for diff stability or hand-editing), the caller must sort before
/// calling <see cref="Save"/> — and should sort a copy rather than the
/// live in-memory collection to avoid surprising callers that share it.
/// </remarks>
public sealed class JsonStore<T> : IJsonStore<T> where T : class, new()
{
    private static readonly ILogger Log = Serilog.Log.ForContext<JsonStore<T>>();

    private readonly string _filePath;
    private readonly string _backupPath;
    private readonly string _label;

    /// <summary>
    /// Creates a store rooted at <see cref="StorageRoot.Directory"/>.
    /// </summary>
    /// <param name="fileName">File name (no directory component), e.g. <c>"paths.json"</c>.</param>
    /// <param name="label">Human-readable noun used in log messages, e.g. <c>"paths"</c>.</param>
    public JsonStore(string fileName, string label)
    {
        _filePath = Path.Combine(StorageRoot.Directory, fileName);
        _backupPath = _filePath + ".bak";
        _label = label;
    }

    public T Load()
    {
        if (TryLoadFile(_filePath, out var value))
            return value;

        if (TryLoadFile(_backupPath, out value))
        {
            Log.Warning("Recovered {Label} from backup {Path}", _label, _backupPath);
            return value;
        }

        Log.Warning("No usable {Label} file found; using defaults", _label);
        return new T();
    }

    public void Save(T value)
    {
        try
        {
            StorageRoot.EnsureExists();
            var json = JsonSerializer.Serialize(value, JsonOptions.Default);
            WriteAtomically(json);
            Log.Information("Saved {Label} to {Path}", _label, _filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save {Label} to {Path}", _label, _filePath);
            throw;
        }
    }

    private bool TryLoadFile(string filePath, out T value)
    {
        value = new T();

        if (!File.Exists(filePath))
            return false;

        try
        {
            var json = File.ReadAllText(filePath);
            value = JsonSerializer.Deserialize<T>(json, JsonOptions.Default) ?? new T();
            Log.Information("Loaded {Label} from {Path}", _label, filePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load {Label} from {Path}", _label, filePath);
            return false;
        }
    }

    private void WriteAtomically(string json)
    {
        var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(tempPath, json);

            if (File.Exists(_filePath))
            {
                File.Replace(tempPath, _filePath, _backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _filePath);
                File.Copy(_filePath, _backupPath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
