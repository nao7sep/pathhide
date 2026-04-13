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
    private readonly string _backupPath;

    public SettingsStore()
    {
        _filePath = Path.Combine(StorageRoot.Directory, "settings.json");
        _backupPath = _filePath + ".bak";
    }

    public AppSettings Load()
    {
        if (TryLoadFile(_filePath, out var settings))
            return settings;

        if (TryLoadFile(_backupPath, out settings))
        {
            Log.Warning("Recovered settings from backup {Path}", _backupPath);
            return settings;
        }

        Log.Warning("No usable settings file found; using defaults");
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            StorageRoot.EnsureExists();
            var json = JsonSerializer.Serialize(settings, JsonOptions.Default);
            WriteAtomically(json);
            Log.Information("Saved settings to {Path}", _filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings to {Path}", _filePath);
            throw;
        }
    }

    private bool TryLoadFile(string filePath, out AppSettings settings)
    {
        settings = new AppSettings();

        if (!File.Exists(filePath))
            return false;

        try
        {
            var json = File.ReadAllText(filePath);
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions.Default) ?? new AppSettings();
            Log.Information("Loaded settings from {Path}", filePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load settings from {Path}", filePath);
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
