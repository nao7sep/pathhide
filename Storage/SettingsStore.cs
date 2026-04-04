using System;
using System.IO;
using System.Text.Json;
using PathHide.Models;
using Serilog;

namespace PathHide.Storage;

public sealed class SettingsStore
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SettingsStore>();

    private readonly string _filePath;

    public SettingsStore()
    {
        _filePath = Path.Combine(StorageRoot.Directory, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            Log.Information("No settings file found at {Path}; using defaults", _filePath);
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions.Default);
            Log.Information("Loaded settings from {Path}", _filePath);
            return settings ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load settings from {Path}; using defaults", _filePath);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            StorageRoot.EnsureExists();
            var json = JsonSerializer.Serialize(settings, JsonOptions.Default);
            File.WriteAllText(_filePath, json);
            Log.Information("Saved settings to {Path}", _filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings to {Path}", _filePath);
            throw;
        }
    }
}
