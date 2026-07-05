using System;
using System.IO;
using System.Text.Json;
using PathHide.Backup;
using PathHide.Services;

namespace PathHide.Storage;

/// <summary>
/// Generic JSON-backed store with atomic replace. One file is written:
/// <c>{file}</c>, the live document, updated by a write-to-temp-then-rename
/// so a crash mid-write never tears it. If the live document is missing, the
/// type's default-constructed value is returned; if it is present but
/// unparseable, it is quarantined (moved aside, original bytes preserved)
/// rather than reset over, and the default-constructed value is returned —
/// see the storage-path conventions' quarantine-then-reset path.
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
        // unreadable (already quarantined and logged a warn above). There is no .bak fallback: a
        // live file that will not parse is moved aside rather than reset over, and its earlier
        // content is recovered, if ever needed, from the quarantined file itself or the
        // data-backup archives (see the data-backup conventions).
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
            // The file exists but will not parse — unexpected, yet recoverable. Quarantine it (move it
            // aside, preserving its bytes) rather than silently falling back to defaults over it, so a
            // later save can never overwrite the user's original bytes — the storage-path conventions'
            // quarantine-then-reset path. The caller falls back to defaults; the file is recreated by the
            // normal first-run materialization path (CreateIfMissing) or the next Save.
            Quarantine(ex);
            return false;
        }
    }

    /// <summary>
    /// Moves the unparseable live file aside to its quarantine name — the derived-filename grammar's
    /// <c>&lt;stem&gt;-&lt;millisecond-utc-stamp&gt;.invalid</c> (see <see cref="QuarantinePath"/>) —
    /// preserving its original bytes, then logs the one warning for this event naming both paths. Never
    /// throws: if the move itself fails (unexpected for a same-directory rename), the corrupt file is left
    /// in place rather than lost, and the warning still fires so the situation stays visible in the log —
    /// a present-but-corrupt file must never block startup (quarantine over halt, per the storage-path
    /// conventions).
    /// </summary>
    private void Quarantine(Exception ex)
    {
        var quarantinePath = QuarantinePath(_filePath, DateTimeOffset.UtcNow);

        try
        {
            File.Move(_filePath, quarantinePath);
        }
        catch (Exception moveEx)
        {
            Log.Warn("store: file unreadable; quarantine move failed, left in place", moveEx,
                new { label = _label, path = _filePath, quarantinePath, readError = ex.Message });
            return;
        }

        Log.Warn("store: file unreadable, quarantined", ex,
            new { label = _label, path = _filePath, quarantinePath });
    }

    private void WriteAtomically(string json)
    {
        var tempPath = TempPath(_filePath, NanoId.New());

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
    /// <c>config.json.&lt;x&gt;.tmp</c>). The discriminator is a <see cref="NanoId"/> (see
    /// <see cref="WriteAtomically"/>); internal so the shape is directly unit-testable without
    /// touching disk.
    /// </summary>
    internal static string TempPath(string targetPath, string discriminator) =>
        Path.Combine(
            Path.GetDirectoryName(targetPath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(targetPath)}-{discriminator}.tmp");

    /// <summary>
    /// The quarantine path for <paramref name="targetPath"/> at <paramref name="timestamp"/>: the target's
    /// stem plus a millisecond UTC stamp, one role extension (<c>.invalid</c>), in the same directory as
    /// the target — the derived-filename grammar's quarantine name (see the storage-path conventions). The
    /// stamp is <see cref="BackupTime.FileStamp"/>, the same <c>yyyyMMdd-HHmmss-fff-utc</c> form the
    /// data-backup engine's archive names use, so the fleet has one timestamp formatter for machine-paced
    /// names rather than two; internal so the shape is directly unit-testable without touching disk.
    /// </summary>
    internal static string QuarantinePath(string targetPath, DateTimeOffset timestamp) =>
        Path.Combine(
            Path.GetDirectoryName(targetPath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(targetPath)}-{BackupTime.FileStamp(timestamp)}.invalid");
}
