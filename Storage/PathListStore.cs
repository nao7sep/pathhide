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

    public PathListStore()
    {
        _filePath = Path.Combine(StorageRoot.Directory, "paths.json");
    }

    public List<PathEntry> Load()
    {
        if (!File.Exists(_filePath))
        {
            Log.Information("No paths file found at {Path}; starting empty", _filePath);
            return [];
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var entries = JsonSerializer.Deserialize<List<PathEntry>>(json, JsonOptions.Default);
            Log.Information("Loaded {Count} path entries from {Path}", entries?.Count ?? 0, _filePath);
            return entries ?? [];
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load paths from {Path}; starting empty", _filePath);
            return [];
        }
    }

    public void Save(List<PathEntry> entries)
    {
        entries.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        try
        {
            StorageRoot.EnsureExists();
            var json = JsonSerializer.Serialize(entries, JsonOptions.Default);
            File.WriteAllText(_filePath, json);
            Log.Information("Saved {Count} path entries to {Path}", entries.Count, _filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save paths to {Path}", _filePath);
            throw;
        }
    }
}
