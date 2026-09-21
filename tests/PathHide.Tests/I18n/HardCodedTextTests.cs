using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace PathHide.Tests.I18n;

/// <summary>
/// The hard-coded-text gate (localization conventions): no sentence a reader sees is written in the
/// source, and no key reaches the source that the catalogue does not have.
///
/// This reads the shipped source as text, which is what a test of a file rather than a module does
/// (tests-folder conventions). It is deliberately narrow: it looks at the properties a person reads
/// or hears — text, a button's content, a menu's header, a window title, a placeholder, a tooltip and
/// the two automation properties — and leaves everything else alone.
/// </summary>
public class HardCodedTextTests
{
    /// <summary>
    /// Literals allowed in a text position: the app's own name, and punctuation and separators, which
    /// read the same in every language.
    /// </summary>
    private static readonly string[] AllowedLiterals =
        ["PathHide", "·", "—", "-", "/", ":", ""];

    private static readonly string[] TextProperties =
        ["Text", "Content", "Header", "Title", "PlaceholderText", "Watermark"];

    [Fact]
    public void no_markup_holds_a_sentence_of_its_own()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(SourceDirectory(), "*.axaml", SearchOption.AllDirectories))
        {
            var markup = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (var property in TextProperties)
            {
                foreach (Match match in Regex.Matches(markup, $@"(?<![\w.]){property}\s*=\s*""([^""]*)"""))
                    Record(offenders, name, property, match.Groups[1].Value);
            }

            foreach (Match match in Regex.Matches(markup, @"ToolTip\.Tip\s*=\s*""([^""]*)"""))
                Record(offenders, name, "ToolTip.Tip", match.Groups[1].Value);

            foreach (Match match in Regex.Matches(markup, @"AutomationProperties\.(Name|HelpText)\s*=\s*""([^""]*)"""))
                Record(offenders, name, "AutomationProperties." + match.Groups[1].Value, match.Groups[2].Value);
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void no_code_assigns_a_sentence_to_a_property_a_reader_sees()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(SourceDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            var code = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            // Text = "…", Content = "…", Title = "…" and their kin, including an interpolated string,
            // which is how a sentence used to be assembled here.
            foreach (var property in TextProperties)
            {
                foreach (Match match in Regex.Matches(code, $@"(?<![\w.]){property}\s*=\s*\$?""([^""]*)"""))
                    Record(offenders, name, property, match.Groups[1].Value);
            }

            foreach (Match match in Regex.Matches(code, @"AutomationProperties\.SetName\([^,]+,\s*\$?""([^""]*)"""))
                Record(offenders, name, "AutomationProperties.SetName", match.Groups[1].Value);

            foreach (Match match in Regex.Matches(code, @"ToolTip\.SetTip\([^,]+,\s*\$?""([^""]*)"""))
                Record(offenders, name, "ToolTip.SetTip", match.Groups[1].Value);
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void every_key_the_source_names_is_in_the_catalogue()
    {
        // Any string in the source shaped like a key under one of the catalogue's own namespaces must
        // be a key. Keys reach the screen through more than the translator's own calls — the shortcut
        // catalogue's helpers, the column headers, the row states — so the check reads every literal
        // rather than a list of call shapes, and a mistyped key fails wherever it is written.
        var english = Keys();
        var namespaces = english.Select(key => key[..key.IndexOf('.')]).ToHashSet(StringComparer.Ordinal);
        var shaped = new Regex(@"""([a-z][a-zA-Z]*)\.([a-zA-Z][a-zA-Z.]*)""");
        var missing = new List<string>();

        foreach (var file in SourceFiles())
        {
            foreach (Match match in shaped.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups[1].Value + "." + match.Groups[2].Value;
                if (namespaces.Contains(match.Groups[1].Value) && !english.Contains(key))
                    missing.Add($"{Path.GetFileName(file)}: {key}");
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void every_dialog_button_is_labelled_by_a_key()
    {
        // A footer button's label goes through the translator, which shows a missing key as the key:
        // a label written as a word ("Close") would reach the screen as that word in every language,
        // and it has no dot, so the key-shaped check above would never see it.
        var english = Keys();
        var labels = SourceFiles()
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"new DialogButton\(\s*""([^""]*)""")
                .Select(match => $"{Path.GetFileName(file)}: {match.Groups[1].Value}"))
            .Where(label => !english.Contains(label[(label.IndexOf(": ", StringComparison.Ordinal) + 2)..]))
            .ToArray();

        Assert.Empty(labels);
    }

    [Fact]
    public void every_key_in_the_catalogue_is_used()
    {
        // A key nothing names is dead weight every translator still has to translate.
        var text = string.Join("\n", SourceFiles().Select(File.ReadAllText));

        var unused = Keys().Where(key => !text.Contains($"\"{key}\"", StringComparison.Ordinal)).ToArray();
        Assert.Empty(unused);
    }

    private static void Record(List<string> offenders, string file, string property, string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || AllowedLiterals.Contains(text))
            return;
        // A binding, a resource, a static reference or a number is not a sentence.
        if (text.StartsWith('{') || text.StartsWith('/') || !text.Any(char.IsLetter))
            return;
        // A single lower-case token is a name or a value, not a sentence a reader reads.
        if (!text.Any(char.IsWhiteSpace) && char.IsLower(text[0]) && !text.Contains('’'))
            return;

        offenders.Add($"{file}: {property}=\"{value}\"");
    }

    private static HashSet<string> Keys()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(CatalogueTests.LocalesDirectory(), "en.json")));
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static string SourceDirectory() => Path.Combine(CatalogueTests.RepoRoot(), "src", "PathHide");

    private static IEnumerable<string> SourceFiles() =>
        Directory.GetFiles(SourceDirectory(), "*.*", SearchOption.AllDirectories)
            .Where(file => Path.GetExtension(file) is ".cs" or ".axaml")
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
}
