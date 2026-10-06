using System.Collections.Generic;
using System.Text.Json;
using PathHide.Models;

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
internal sealed class PathListDocument
{
    public List<PathEntry> Paths
    {
        get;
        init => field = value ?? throw new JsonException("The path list file holds null instead of its paths.");
    } = [];
}
