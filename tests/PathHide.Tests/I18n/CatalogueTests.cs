using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using PathHide.I18n;
using Xunit;

namespace PathHide.Tests.I18n;

/// <summary>
/// The catalogue gate (localization conventions): English defines the key set, every language has
/// exactly it, placeholders and plural forms match, nothing is left in English by accident, and no
/// file carries a character a reader cannot see.
///
/// These read the files in the repository rather than the embedded copies, because two of the checks —
/// the hidden characters and the trailing newline — are about the file itself, which parsing erases.
/// </summary>
public class CatalogueTests
{
    /// <summary>
    /// Values a language shares with English on purpose: a brand, a word that language borrows whole,
    /// or a token. A listed entry that no longer matches English fails too, so the list cannot rot.
    /// </summary>
    private static readonly Dictionary<string, string[]> SameAsEnglish = new()
    {
        // "Version", "Format" and "Navigation" are the German words.
        ["de"] = ["about.version", "grid.family", "shortcuts.groupNavigation"],
        // "Error" and "Visible" are the Spanish words, and so their singular counts; "Actual" means
        // "current", which is exactly that column; Apple's Spanish Window menu says "Zoom".
        ["es"] = ["actual.error", "actual.visible", "apply.errors", "grid.actual", "nativeMenu.zoom", "status.visible"],
        // French writes Version, Visible, Format, Menu, Navigation and Actions the same way, and
        // Apple's French app menu says "Services".
        ["fr"] = [
            "about.version", "actual.visible", "grid.family", "menu.button", "nativeMenu.services",
            "shortcuts.groupNavigation", "status.visible", "toolbar.actions"],
        // "File" and "Menu" are Italian words.
        ["it"] = ["kind.file", "menu.button"],
        // "Menu" is the Portuguese word, and Apple's Brazilian Window menu says "Zoom".
        ["pt-BR"] = ["menu.button", "nativeMenu.zoom"],
        ["ru"] = [],
        ["ja"] = [],
        ["ko"] = [],
        ["zh-Hans"] = [],
    };

    // Brand and product names, and the names of standards, are the same everywhere: GitHub, and the
    // three path families the list tells apart, which are named for the systems that define them.
    private static readonly string[] BrandKeys = ["about.github", "family.posix", "family.windows", "family.unc"];

    private static readonly string[] Tags =
        ["en", "de", "es", "fr", "it", "pt-BR", "ru", "ja", "ko", "zh-Hans"];

    [Fact]
    public void the_set_is_exactly_the_ten_interface_languages()
    {
        var files = Directory.GetFiles(LocalesDirectory(), "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Tags.OrderBy(tag => tag, StringComparer.Ordinal).ToArray(), files);
        Assert.Equal(Tags.OrderBy(tag => tag, StringComparer.Ordinal), Languages.Tags.OrderBy(tag => tag, StringComparer.Ordinal));
    }

    [Fact]
    public void every_language_has_exactly_english_keys()
    {
        var english = Read("en").Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        foreach (var tag in Tags)
        {
            var keys = Read(tag).Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
            Assert.Equal(english, keys);
        }
    }

    [Fact]
    public void every_value_is_present_and_trimmed()
    {
        foreach (var tag in Tags)
        {
            foreach (var (key, entry) in Read(tag))
            {
                foreach (var (form, text) in Forms(entry))
                {
                    Assert.False(string.IsNullOrWhiteSpace(text), $"{tag}: {key}{form} is empty.");
                    Assert.Equal(text.Trim(), text);
                }
            }
        }
    }

    [Fact]
    public void plural_entries_use_exactly_their_language_categories()
    {
        var english = Read("en");
        foreach (var tag in Tags)
        {
            var expected = Plural.CategoriesOf(tag).OrderBy(form => form, StringComparer.Ordinal).ToArray();
            foreach (var (key, entry) in Read(tag))
            {
                if (english[key].ValueKind != JsonValueKind.Object)
                {
                    Assert.NotEqual(JsonValueKind.Object, entry.ValueKind);
                    continue;
                }

                Assert.Equal(JsonValueKind.Object, entry.ValueKind);
                var forms = entry.EnumerateObject()
                    .Select(form => form.Name)
                    .OrderBy(form => form, StringComparer.Ordinal)
                    .ToArray();
                Assert.Equal(expected, forms);
            }
        }
    }

    [Fact]
    public void every_form_keeps_english_placeholders()
    {
        var english = Read("en");
        foreach (var tag in Tags.Where(tag => tag != "en"))
        {
            foreach (var (key, entry) in Read(tag))
            {
                var expected = Placeholders(english[key]);
                foreach (var (form, text) in Forms(entry))
                {
                    Assert.Equal(expected, PlaceholdersIn(text));
                }
            }
        }
    }

    [Fact]
    public void no_language_copies_english()
    {
        var english = Read("en");
        foreach (var tag in Tags.Where(tag => tag != "en"))
        {
            var allowed = SameAsEnglish[tag];
            var matched = new List<string>();
            foreach (var (key, entry) in Read(tag))
            {
                if (BrandKeys.Contains(key))
                    continue;

                foreach (var (form, text) in Forms(entry))
                {
                    // Only a value with words of its own can be a missed translation. An entry that is
                    // nothing but placeholders and punctuation — the one that joins two finished
                    // sentences, for instance — is identical everywhere by nature, and the letters
                    // inside {added} are the placeholder's name, not text anyone reads.
                    if (!Regex.Replace(text, @"\{[a-zA-Z]+\}", "").Any(char.IsLetter))
                        continue;
                    if (FormOf(english[key], form) != text)
                        continue;

                    Assert.Contains(key, allowed);
                    matched.Add(key);
                }
            }

            // A listed key that no longer matches English is stale and must go.
            foreach (var key in allowed)
                Assert.Contains(key, matched);
        }
    }

    [Fact]
    public void no_file_holds_a_character_the_reader_cannot_see()
    {
        // Read as text, not as JSON: parsing makes an escape and the literal character the same, so
        // this is the only place a literal no-break space can be caught (hidden-character conventions).
        // The escapes stay escapes: a verbatim string hands them to the regex engine instead of
        // putting the characters themselves into this file.
        var hidden = new Regex(
            @"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F\u00A0\u00AD\u2007\u200B-\u200F\u2028\u2029\u202A-\u202F\u2060\u2066-\u2069\uFEFF]");

        foreach (var tag in Tags)
        {
            var path = Path.Combine(LocalesDirectory(), tag + ".json");
            var text = File.ReadAllText(path);
            var found = hidden.Match(text);
            if (found.Success)
            {
                Assert.Fail(
                    $"{tag}.json holds U+{(int)found.Value[0]:X4} at offset {found.Index}; write it as an escape.");
            }
            Assert.EndsWith("\n", text);
        }
    }

    [Fact]
    public void the_macos_bundle_declares_every_language()
    {
        var plist = File.ReadAllText(Path.Combine(RepoRoot(), "macOS", "Info.plist"));
        var declared = Regex.Matches(plist, @"<key>CFBundleLocalizations</key>\s*<array>(.*?)</array>",
            RegexOptions.Singleline);
        Assert.True(declared.Count == 1, "Info.plist should declare CFBundleLocalizations exactly once.");

        var listed = Regex.Matches(declared[0].Groups[1].Value, @"<string>(.*?)</string>")
            .Select(match => match.Groups[1].Value)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(Tags.OrderBy(tag => tag, StringComparer.Ordinal).ToArray(), listed);
    }

    [Fact]
    public void every_language_words_the_macos_permission_prompts()
    {
        // macOS shows these when PathHide first reaches a protected folder or volume, taking them from
        // the bundle's localization rather than from the app, so they live beside Info.plist and not
        // in the catalogue. English is Info.plist's own words; every other language has its own.
        var plist = File.ReadAllText(Path.Combine(RepoRoot(), "macOS", "Info.plist"));
        var english = Regex.Matches(plist, @"<key>(NS\w+UsageDescription)</key>\s*<string>(.*?)</string>")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);
        Assert.NotEmpty(english);

        var folders = Directory.GetDirectories(Path.Combine(RepoRoot(), "macOS"), "*.lproj")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(name => name, StringComparer.Ordinal);
        Assert.Equal(Tags.OrderBy(tag => tag, StringComparer.Ordinal), folders);

        var hidden = new Regex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F\u00A0\u00AD\u2007\u200B-\u200F\u2028\u2029\u202A-\u202F\u2060\u2066-\u2069\uFEFF]");
        foreach (var tag in Tags)
        {
            var path = Path.Combine(RepoRoot(), "macOS", tag + ".lproj", "InfoPlist.strings");
            var text = File.ReadAllText(path);
            Assert.False(hidden.IsMatch(text), $"{tag}: InfoPlist.strings holds a character the reader cannot see.");

            var strings = Regex.Matches(text, @"^""(\w+)""\s*=\s*""((?:[^""\\]|\\.)*)"";\s*$", RegexOptions.Multiline)
                .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);
            Assert.Equal(english.Keys.OrderBy(key => key, StringComparer.Ordinal), strings.Keys.OrderBy(key => key, StringComparer.Ordinal));

            foreach (var (key, value) in strings)
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"{tag}: {key} is empty.");
                Assert.Equal(value.Trim(), value);
                Assert.Contains("PathHide", value, StringComparison.Ordinal);
                if (tag == "en")
                    Assert.Equal(english[key], value);
                else
                    Assert.NotEqual(english[key], value);
            }
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> Read(string tag)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(LocalesDirectory(), tag + ".json")));
        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone());
    }

    private static IEnumerable<(string Form, string Text)> Forms(JsonElement entry)
    {
        if (entry.ValueKind == JsonValueKind.Object)
        {
            foreach (var form in entry.EnumerateObject())
                yield return ($" [{form.Name}]", form.Value.GetString() ?? "");
            yield break;
        }

        yield return ("", entry.GetString() ?? "");
    }

    private static string? FormOf(JsonElement entry, string form) =>
        entry.ValueKind == JsonValueKind.Object
            ? entry.EnumerateObject()
                .Where(property => $" [{property.Name}]" == form)
                .Select(property => property.Value.GetString())
                .FirstOrDefault()
            : entry.GetString();

    private static IReadOnlyList<string> Placeholders(JsonElement entry) =>
        Forms(entry).SelectMany(form => PlaceholdersIn(form.Text)).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> PlaceholdersIn(string text) =>
        Regex.Matches(text, @"\{([a-zA-Z]+)\}")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    internal static string LocalesDirectory() =>
        Path.Combine(RepoRoot(), "src", "PathHide", "I18n", "Locales");

    internal static string RepoRoot([CallerFilePath] string callerPath = "")
    {
        // This file: <repo>/tests/PathHide.Tests/I18n/CatalogueTests.cs
        var directory = Path.GetDirectoryName(callerPath)!;
        return Path.GetFullPath(Path.Combine(directory, "..", "..", ".."));
    }
}
