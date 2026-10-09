using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PathHide.I18n;
using PathHide.Models;

namespace PathHide.Storage;

/// <summary>The config set keys, how each is read, and how each is written (config-sets-conventions).</summary>
/// <remarks>
/// Retirement policy: all four sets are harmless preferences. An invalid value falls back to its
/// built-in and is normalized at the next Settings save that changes something; a key PathHide does not
/// know (only possible from a newer build or a hand edit) is dropped at that save, because the file is
/// written from <see cref="Differing"/> alone.
/// </remarks>
internal static class SettingsSets
{
    internal const string Language = "language";
    internal const string UiFontFamily = "uiFontFamily";
    internal const string Theme = "theme";
    internal const string WindowsHideMode = "windowsHideMode";

    // Settings only: the shared options also read paths.json, whose enums keep their own reading.
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    internal static AppSettings Read(IReadOnlyDictionary<string, JsonElement> sets, out List<string> invalidKeys)
    {
        var invalid = new List<string>();
        var defaults = new AppSettings();
        var settings = new AppSettings
        {
            Language = ReadText(Language, Languages.ParsePreference) ?? defaults.Language,
            UiFontFamily = ReadText(UiFontFamily, UiFontFamilyValue.Normalize) ?? defaults.UiFontFamily,
            Theme = ReadEnum(Theme, defaults.Theme),
            WindowsHideMode = ReadEnum(WindowsHideMode, defaults.WindowsHideMode),
        };
        invalidKeys = invalid;
        return settings;

        string? ReadText(string key, Func<string, string?> parse)
        {
            if (!sets.TryGetValue(key, out var copy))
                return null;
            if (copy.ValueKind == JsonValueKind.String && parse(copy.GetString()!) is { } value)
                return value;
            invalid.Add(key);
            return null;
        }

        T ReadEnum<T>(string key, T builtIn) where T : struct, Enum
        {
            if (!sets.TryGetValue(key, out var copy))
                return builtIn;
            try
            {
                // The converter combines a comma-separated list into a value no member names.
                var value = copy.Deserialize<T>(Options);
                if (Enum.IsDefined(value))
                    return value;
            }
            catch (JsonException)
            {
            }
            invalid.Add(key);
            return builtIn;
        }
    }

    /// <summary>The sets of <paramref name="settings"/> that differ from their built-ins, each as written.</summary>
    internal static Dictionary<string, JsonElement> Differing(AppSettings settings)
    {
        var builtIn = new AppSettings();
        var sets = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        // Text sets compare after their cleanup (text-cleanup-conventions).
        Add(Language, Languages.NormalizePreference(settings.Language), builtIn.Language);
        Add(UiFontFamily, UiFontFamilyValue.Normalize(settings.UiFontFamily), builtIn.UiFontFamily);
        Add(Theme, settings.Theme, builtIn.Theme);
        Add(WindowsHideMode, settings.WindowsHideMode, builtIn.WindowsHideMode);
        return sets;

        void Add<T>(string key, T value, T builtInValue)
        {
            if (!EqualityComparer<T>.Default.Equals(value, builtInValue))
                sets[key] = JsonSerializer.SerializeToElement(value, Options);
        }
    }

    internal static bool SameSets(IReadOnlyDictionary<string, JsonElement> left, IReadOnlyDictionary<string, JsonElement> right) =>
        left.Count == right.Count
        && left.All(set => right.TryGetValue(set.Key, out var other) && other.GetRawText() == set.Value.GetRawText());
}
