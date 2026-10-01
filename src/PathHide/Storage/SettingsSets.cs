using System;
using System.Collections.Generic;
using System.Text.Json;
using PathHide.I18n;
using PathHide.Models;

namespace PathHide.Storage;

/// <summary>The config set keys, their read shapes, and the dialog's change comparison.</summary>
internal static class SettingsSets
{
    internal const string Language = "language";
    internal const string UiFontFamily = "uiFontFamily";
    internal const string Theme = "theme";
    internal const string WindowsHideMode = "windowsHideMode";

    internal static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.Ordinal)
        { Language, UiFontFamily, Theme, WindowsHideMode };

    internal static AppSettings Read(IReadOnlyDictionary<string, JsonElement> sets, out List<string> invalidKeys)
    {
        var invalid = new List<string>();
        var defaults = new AppSettings();
        var settings = new AppSettings
        {
            Language = ReadSet(Language, defaults.Language),
            UiFontFamily = ReadSet(UiFontFamily, defaults.UiFontFamily),
            Theme = ReadSet(Theme, defaults.Theme),
            WindowsHideMode = ReadSet(WindowsHideMode, defaults.WindowsHideMode),
        };
        invalidKeys = invalid;
        return settings;

        T ReadSet<T>(string key, T builtIn)
        {
            if (!sets.TryGetValue(key, out var copy))
                return builtIn;

            // All four sets are strings on disk, including the snake_case enum values.
            if (copy.ValueKind == JsonValueKind.String)
            {
                try
                {
                    var value = copy.Deserialize<T>(JsonOptions.Default);
                    if (value is not null && (!typeof(T).IsEnum
                        || (Enum.IsDefined(typeof(T), value)
                            && string.Equals(copy.GetString(),
                                JsonSerializer.SerializeToElement(value, JsonOptions.Default).GetString(),
                                StringComparison.OrdinalIgnoreCase))))
                        return value;
                }
                catch (JsonException)
                {
                    // A malformed copy affects this set only; the surrounding map is still usable.
                }
            }

            invalid.Add(key);
            return builtIn;
        }
    }

    internal static Dictionary<string, JsonElement> Changes(AppSettings previous, AppSettings current)
    {
        var changes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (Languages.NormalizePreference(previous.Language) != Languages.NormalizePreference(current.Language))
            changes[Language] = JsonSerializer.SerializeToElement(current.Language, JsonOptions.Default);
        if (UiFontFamilyValue.Normalize(previous.UiFontFamily) != UiFontFamilyValue.Normalize(current.UiFontFamily))
            changes[UiFontFamily] = JsonSerializer.SerializeToElement(current.UiFontFamily, JsonOptions.Default);
        if (previous.Theme != current.Theme)
            changes[Theme] = JsonSerializer.SerializeToElement(current.Theme, JsonOptions.Default);
        if (previous.WindowsHideMode != current.WindowsHideMode)
            changes[WindowsHideMode] = JsonSerializer.SerializeToElement(current.WindowsHideMode, JsonOptions.Default);
        return changes;
    }
}
