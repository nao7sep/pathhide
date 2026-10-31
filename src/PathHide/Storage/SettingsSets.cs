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
/// An invalid value reads as its built-in and is kept as written by every save until the user sets that
/// setting; a key PathHide does not know (from a hand edit or another build) is always kept
/// (<see cref="Kept"/>, config-sets-conventions). No key has been retired.
/// </remarks>
internal static class SettingsSets
{
    internal const string Language = "language";
    internal const string UiFontFamily = "uiFontFamily";
    internal const string Theme = "theme";
    internal const string WindowsHideMode = "windowsHideMode";
    internal const string DisplayTimeZone = "displayTimeZone";

    private static readonly HashSet<string> Known =
        new([Language, UiFontFamily, Theme, WindowsHideMode, DisplayTimeZone], StringComparer.Ordinal);

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
            DisplayTimeZone = ReadText(DisplayTimeZone, DisplayZone.ParsePreference) ?? defaults.DisplayTimeZone,
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
        // Text sets compare after their cleanup, so stray spaces or case never make a set differ from
        // its built-in and land in config.json.
        Add(Language, Languages.NormalizePreference(settings.Language), builtIn.Language);
        Add(UiFontFamily, UiFontFamilyValue.Normalize(settings.UiFontFamily), builtIn.UiFontFamily);
        Add(Theme, settings.Theme, builtIn.Theme);
        Add(WindowsHideMode, settings.WindowsHideMode, builtIn.WindowsHideMode);
        Add(DisplayTimeZone, DisplayZone.NormalizePreference(settings.DisplayTimeZone), builtIn.DisplayTimeZone);
        return sets;

        void Add<T>(string key, T value, T builtInValue)
        {
            if (!EqualityComparer<T>.Default.Equals(value, builtInValue))
                sets[key] = JsonSerializer.SerializeToElement(value, Options);
        }
    }

    /// <summary>
    /// What a save keeps from the <paramref name="stored"/> sets beside the <paramref name="written"/>
    /// ones: every key PathHide does not know, and each known key whose stored value is invalid while the
    /// user's value for it is still the built-in it read as, so it is not in <paramref name="written"/>.
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, JsonElement>> Kept(
        IReadOnlyDictionary<string, JsonElement> stored, IReadOnlyDictionary<string, JsonElement> written)
    {
        Read(stored, out var invalidKeys);
        return stored
            .Where(set => !Known.Contains(set.Key) || (invalidKeys.Contains(set.Key) && !written.ContainsKey(set.Key)))
            .Select(set => KeyValuePair.Create(set.Key, set.Value.Clone()))
            .ToList();
    }

    internal static bool SameSets(IReadOnlyDictionary<string, JsonElement> left, IReadOnlyDictionary<string, JsonElement> right) =>
        left.Count == right.Count
        && left.All(set => right.TryGetValue(set.Key, out var other) && other.GetRawText() == set.Value.GetRawText());
}
