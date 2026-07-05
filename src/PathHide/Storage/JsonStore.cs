using System;
using System.IO;
using System.Text.Json;
using PathHide.Services;

namespace PathHide.Storage;

/// <summary>
/// Generic JSON-backed store with atomic replace. One file is written:
/// <c>{file}</c>, the live document, updated by a write-to-temp-then-rename
/// so a crash mid-write never tears it. If the live document is missing or
/// unparseable on load, the type's default-constructed value is returned.
/// </summary>
/// <remarks>
/// There is no <c>.bak</c> last-good sidecar: its only job was recovery, and
/// recovery is now split cleanly (per the data-backup conventions) — the
/// atomic write prevents the torn write the sidecar guarded against, and the
/// point-in-time history it approximated with a single previous copy lives in
/// the startup data-backup archives under <c>~/.pathhide/backups/</c>.
/// </remarks>
/// <remarks>
/// Caller responsibilities: this store does not impose any ordering or
/// canonicalisation on the value it receives. If on-disk ordering matters
/// (for diff stability or hand-editing), the caller must sort before
/// calling <see cref="Save"/> — and should sort a copy rather than the
/// live in-memory collection to avoid surprising callers that share it.
/// </remarks>
public sealed class JsonStore<T> : IJsonStore<T> where T : class, new()
{
    private readonly string _filePath;
    private readonly string _label;

    /// <summary>
    /// Creates a store rooted at <see cref="StorageRoot.Directory"/>.
    /// </summary>
    /// <param name="fileName">File name (no directory component), e.g. <c>"paths.json"</c>.</param>
    /// <param name="label">Human-readable noun used in log messages, e.g. <c>"paths"</c>.</param>
    public JsonStore(string fileName, string label)
    {
        _filePath = Path.Combine(StorageRoot.Directory, fileName);
        _label = label;
    }

    public T Load()
    {
        if (TryLoadFile(out var value))
            return value;

        // Reached on first run (no file yet — normal) or after the live file was present but
        // unreadable (already logged a warn above). There is no .bak fallback: a live file that
        // will not parse falls back to defaults, and its earlier content is recovered, if ever
        // needed, from the data-backup archives (see the data-backup conventions).
        Log.Info("store: no existing data, using defaults", new { label = _label });
        return new T();
    }

    public void Save(T value)
    {
        try
        {
            StorageRoot.EnsureExists();
            var json = JsonSerializer.Serialize(value, JsonOptions.Default);
            WriteAtomically(json);
            Log.Info("store: saved", new { label = _label, path = _filePath });
        }
        catch (Exception ex)
        {
            Log.Error("store: save failed", ex, new { label = _label, path = _filePath });
            throw;
        }
    }

    public bool CreateIfMissing(T value)
    {
        // Absence is the single trigger. An existing file — including one that is present but
        // unparseable — is never overwritten, so a good (possibly hand-edited) file can never be lost to
        // a bug here. The file is produced through Save (the same serializer the normal save path uses),
        // not a hand-built literal.
        if (File.Exists(_filePath))
            return false;

        Save(value);
        return true;
    }

    private bool TryLoadFile(out T value)
    {
        value = new T();

        // An absent file is normal (first run): not a failure, so it is not logged here — the
        // caller decides what the absence means.
        if (!File.Exists(_filePath))
            return false;

        try
        {
            var json = File.ReadAllText(_filePath);
            value = JsonSerializer.Deserialize<T>(json, JsonOptions.Default) ?? new T();
            Log.Info("store: loaded", new { label = _label, path = _filePath });
            return true;
        }
        catch (Exception ex)
        {
            // The file exists but will not parse — unexpected, yet recoverable (the caller falls back
            // to defaults, and history lives in the data-backup archives), so warn rather than error.
            Log.Warn("store: file unreadable", ex, new { label = _label, path = _filePath });
            return false;
        }
    }

    private void WriteAtomically(string json)
    {
        var tempPath = TempPath(_filePath, Guid.NewGuid().ToString("N"));

        try
        {
            File.WriteAllText(tempPath, json);

            // A pure atomic temp-then-rename with no .bak sidecar: replace the existing file in place, or
            // move the temp into a fresh one. This is the durability floor (the storage-path conventions);
            // point-in-time history lives in the data-backup archives, not a last-good copy beside the file.
            if (File.Exists(_filePath))
            {
                File.Replace(tempPath, _filePath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>
    /// The atomic-write temp path for <paramref name="targetPath"/>: the target's stem plus
    /// <paramref name="discriminator"/>, one role extension (<c>.tmp</c>), in the same directory as the
    /// target — the derived-filename grammar, never a dot-appended suffix (e.g. never
    /// <c>config.json.&lt;x&gt;.tmp</c>). This app has no nanoid utility yet, so the discriminator is a
    /// GUID (see <see cref="WriteAtomically"/>); internal so the shape is directly unit-testable without
    /// touching disk.
    /// </summary>
    internal static string TempPath(string targetPath, string discriminator) =>
        Path.Combine(
            Path.GetDirectoryName(targetPath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(targetPath)}-{discriminator}.tmp");
}
