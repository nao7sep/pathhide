using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using PathHide.Models;
using PathHide.Services;

namespace PathHide.Storage;

/// <summary>
/// The path list, <c>paths.json</c>, read and written through the managed atomic JSON store. The file is
/// an object holding the entries under <c>paths</c>, so it can record its format version beside them.
/// </summary>
/// <remarks>
/// The list is the user's work product, re-derivable from nothing else on disk: a file that cannot be read
/// is left in place, and every load and save throws <see cref="UnreadableStoreException"/> naming it until
/// the user repairs or moves it (store-recovery-conventions).
/// </remarks>
public sealed class PathListStore : IJsonStore<List<PathEntry>>
{
    public const string FileName = "paths.json";

    private readonly JsonStore<PathListDocument> _store =
        new(FileName, QuarantineJournal.PathListLabel, FormatVersions.PathList, haltWhenUnreadable: true);

    public LoadedStore<List<PathEntry>> Load()
    {
        var loaded = _store.Load();
        return new LoadedStore<List<PathEntry>>(loaded.Value.Paths, loaded.WasUnreadable);
    }

    public void Save(List<PathEntry> value) => _store.Save(new PathListDocument { Paths = value });
}

/// <summary>What <c>paths.json</c> holds beside its format version.</summary>
/// <remarks>
/// Every entry must be one the app could have added: an absolute path in any family the grammar accepts,
/// whichever platform reads it, with a defined desired visibility. Anything else makes the file
/// unreadable; nothing is repaired, and whether the path exists is not this check's business.
/// </remarks>
internal sealed class PathListDocument
{
    [JsonRequired]
    public List<PathEntry> Paths
    {
        get;
        init => field = Validated(value);
    } = [];

    private static List<PathEntry> Validated(List<PathEntry>? paths)
    {
        if (paths is null)
            throw new JsonException("The path list file holds null instead of its paths.");

        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in paths)
        {
            if (entry?.Path is null
                || !PathNormalizer.TryNormalize(entry.Path, out var normalized, out var family)
                || !Enum.IsDefined(entry.DesiredVisibility))
                throw new JsonException("The path list file holds an entry that is not an absolute path with a defined visibility.");
            if (!identities.Add($"{family}:{normalized}"))
                throw new JsonException("The path list file holds duplicate path identities.");
        }

        return paths;
    }
}
