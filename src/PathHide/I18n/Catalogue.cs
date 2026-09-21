using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;

namespace PathHide.I18n;

/// <summary>
/// One language's text: flat dotted keys, each holding either a sentence or, where a count changes
/// the words around it, one sentence per plural form.
///
/// The files ship inside the assembly, so the catalogues cannot be separated from the build that was
/// tested against them, and a language change never touches the disk.
/// </summary>
internal sealed class Catalogue
{
    /// <summary>A key's text: <see cref="Text"/> alone, or one <see cref="Forms"/> entry per plural form.</summary>
    internal readonly record struct Entry(string? Text, IReadOnlyDictionary<string, string>? Forms)
    {
        internal bool IsPlural => Forms is not null;
    }

    private readonly IReadOnlyDictionary<string, Entry> _entries;

    internal string Tag { get; }

    private Catalogue(string tag, IReadOnlyDictionary<string, Entry> entries)
    {
        Tag = tag;
        _entries = entries;
    }

    internal IReadOnlyCollection<string> Keys => (IReadOnlyCollection<string>)_entries.Keys;

    internal bool TryGet(string key, out Entry entry) => _entries.TryGetValue(key, out entry);

    private static readonly Dictionary<string, Catalogue> s_loaded = [];
    private static readonly Lock s_gate = new();

    /// <summary>The catalogue for <paramref name="tag"/>, read once per process.</summary>
    internal static Catalogue For(string tag)
    {
        lock (s_gate)
        {
            if (s_loaded.TryGetValue(tag, out var loaded))
                return loaded;
            var catalogue = Parse(tag, ReadResource(tag));
            s_loaded[tag] = catalogue;
            return catalogue;
        }
    }

    /// <summary>The resource name a language's file ships under.</summary>
    internal static string ResourceNameOf(string tag) => $"PathHide.I18n.Locales.{tag}.json";

    private static string ReadResource(string tag)
    {
        var name = ResourceNameOf(tag);
        using var stream = typeof(Catalogue).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The catalogue {name} is not in the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Reads a catalogue from its JSON. Used by the tests over the files on disk.</summary>
    internal static Catalogue Parse(string tag, string json)
    {
        using var document = JsonDocument.Parse(json);
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            entries[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => new Entry(property.Value.GetString(), null),
                JsonValueKind.Object => new Entry(null, ReadForms(property.Value)),
                _ => throw new InvalidOperationException(
                    $"{tag}: {property.Name} is neither a sentence nor a set of plural forms."),
            };
        }

        return new Catalogue(tag, entries);
    }

    private static IReadOnlyDictionary<string, string> ReadForms(JsonElement element)
    {
        var forms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var form in element.EnumerateObject())
            forms[form.Name] = form.Value.GetString() ?? "";
        return forms;
    }

    /// <summary>Every catalogue in the set, for the gates.</summary>
    internal static IEnumerable<Catalogue> All()
    {
        foreach (var tag in Languages.Tags)
            yield return For(tag);
    }

    /// <summary>
    /// The catalogue files as they sit in the repository, for the gates that must read the file rather
    /// than its parsed form, such as the hidden-character check. Null when the sources are not beside
    /// the test run, which only happens off a working tree.
    /// </summary>
    internal static string? SourceDirectory(string? startingAt = null)
    {
        var directory = new DirectoryInfo(startingAt ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            var locales = Path.Combine(directory.FullName, "src", "PathHide", "I18n", "Locales");
            if (Directory.Exists(locales))
                return locales;
            directory = directory.Parent;
        }

        return null;
    }
}
