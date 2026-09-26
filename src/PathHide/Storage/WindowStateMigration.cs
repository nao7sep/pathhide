using System;
using System.IO;
using System.Text.Json;
using PathHide.Models;
using PathHide.Services;

namespace PathHide.Storage;

/// <summary>
/// Moves the window geometry that earlier versions kept in <c>config.json</c> into
/// <c>state.json</c>, once.
/// </summary>
/// <remarks>
/// The trigger is the old fields still being in <c>config.json</c>. The move writes
/// <c>state.json</c> only when it is absent, then rewrites <c>config.json</c> through the settings
/// store, whose type no longer carries the fields, so the next launch finds nothing to move. A
/// failure between the two steps repeats the move next launch without overwriting the state written
/// the first time.
/// </remarks>
public static class WindowStateMigration
{
    /// <summary>Runs the move, if there is one to make. Returns whether it made one.</summary>
    /// <param name="configPath">The live <c>config.json</c>, read as it is on disk.</param>
    /// <param name="settings">The settings the store just loaded from that file.</param>
    public static bool Run(
        string configPath,
        IJsonStore<AppState> stateStore,
        IJsonStore<AppSettings> settingsStore,
        AppSettings settings)
    {
        if (!File.Exists(configPath))
            return false;

        var legacy = ReadLegacyGeometry(File.ReadAllText(configPath));
        if (legacy is null)
            return false;

        stateStore.CreateIfMissing(legacy);
        settingsStore.Save(settings);
        Log.Info("state: moved the window geometry from config.json to state.json");
        return true;
    }

    /// <summary>The properties earlier versions wrote into <c>config.json</c> for the window.</summary>
    private static readonly string[] LegacyProperties =
        ["windowPositionX", "windowPositionY", "windowWidth", "windowHeight", "windowMaximized"];

    /// <summary>
    /// The window geometry an earlier version wrote into <paramref name="configJson"/>, or null when
    /// it carries none of those properties (or is not a JSON object). Present with a null value still
    /// counts, so the rewrite clears it.
    /// </summary>
    internal static AppState? ReadLegacyGeometry(string configJson)
    {
        using var document = JsonDocument.Parse(configJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !Array.Exists(LegacyProperties, name => root.TryGetProperty(name, out _)))
        {
            return null;
        }

        return new AppState
        {
            WindowPositionX = Int(root, "windowPositionX"),
            WindowPositionY = Int(root, "windowPositionY"),
            WindowWidth = Double(root, "windowWidth"),
            WindowHeight = Double(root, "windowHeight"),
            WindowMaximized = root.TryGetProperty("windowMaximized", out var maximized)
                && maximized.ValueKind == JsonValueKind.True,
        };
    }

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number) ? number : null;

    private static double? Double(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : null;
}
