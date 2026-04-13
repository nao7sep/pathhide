using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PathHide.Models;
using Serilog;

namespace PathHide.Storage;

public sealed class PathListStore
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PathListStore>();

    private readonly string _filePath;
    private readonly string _backupPath;

    public PathListStore()
    {
        _filePath = Path.Combine(StorageRoot.Directory, "paths.json");
        _backupPath = _filePath + ".bak";
    }

    public List<PathEntry> Load()
    {
        if (TryLoadFile(_filePath, out var entries))
            return entries;

        if (TryLoadFile(_backupPath, out entries))
        {
            Log.Warning("Recovered path entries from backup {Path}", _backupPath);
            return entries;
        }

        Log.Warning("No usable path list found; starting empty");
        return [];
    }

    public void Save(List<PathEntry> entries)
    {
        entries.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        try
        {
            StorageRoot.EnsureExists();
            var json = JsonSerializer.Serialize(entries, JsonOptions.Default);
            WriteAtomically(json);
            Log.Information("Saved {Count} path entries to {Path}", entries.Count, _filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save paths to {Path}", _filePath);
            throw;
        }
    }

    private bool TryLoadFile(string filePath, out List<PathEntry> entries)
    {
        entries = [];

        if (!File.Exists(filePath))
            return false;

        try
        {
            var json = File.ReadAllText(filePath);
            entries = JsonSerializer.Deserialize<List<PathEntry>>(json, JsonOptions.Default) ?? [];
            Log.Information("Loaded {Count} path entries from {Path}", entries.Count, filePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load path entries from {Path}", filePath);
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
