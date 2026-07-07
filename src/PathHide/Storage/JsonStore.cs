using System;
using System.IO;
using System.Text;
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
/// This is the app's single managed-text atomic-write choke point, and so the
/// one place the data-backup hook lives. <see cref="WriteAtomically"/> records
/// the exact bytes it just wrote into <see cref="BackupStore"/> strictly AFTER
/// the rename lands; a managed-text write that bypasses this store is a silent
/// backup gap, so there is deliberately no second atomic-write path in the app.
/// </remarks>
/// <remarks>
/// There is no <c>.bak</c> last-good sidecar: its only job was recovery, and
/// recovery is now split cleanly (per the data-backup conventions) — the
/// atomic write prevents the torn write the sidecar guarded against, and the
/// point-in-time version history it approximated with a single previous copy
/// lives in the write-through store <c>~/.pathhide/backups.sqlite3</c>.
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
        // write-through backup store backups.sqlite3 (see the data-backup conventions).
        Log.Info("store: no existing data, using defaults", new { label = _label });
        return new T();
    }

    public void Save(T value)
    {
        try
        {
            StorageRoot.EnsureExists();
            var json = JsonSerializer.Serialize(value, JsonOptions.Default);
            // Encode once, here, so the exact bytes written to disk are the exact bytes recorded to the
            // backup store after the rename (no re-encode, no re-read). No BOM: File.WriteAllText/Encoding
            // .UTF8 without a preamble matches what the app writes and reads back.
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
            WriteAtomically(bytes);
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
            // not recorded: this is a move-aside of an already-unreadable managed file, not a managed-text
            // write — no new content is produced here, and the corrupt bytes are not a version to preserve
            // (the store never captured them, so there is nothing to add). The subsequent fresh save through
            // WriteAtomically is what records the recovered-to-defaults content (data-backup conventions).
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

    private void WriteAtomically(byte[] bytes)
    {
        var tempPath = TempPath(_filePath, NanoId.New());

        try
        {
            // not recorded: this temp is atomic-write scratch under the derived-filename grammar, never a
            // managed-text destination — it is renamed away (or deleted) before anything reads it, and the
            // record fires only on the final file below. It is written directly, not through this store.
            File.WriteAllBytes(tempPath, bytes);

            // A pure atomic temp-then-rename with no .bak sidecar: replace the existing file in place, or
            // move the temp into a fresh one. This is the durability floor (the storage-path conventions);
            // point-in-time history lives in the write-through backup store, not a last-good copy beside
            // the file.
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

        // Strictly AFTER the rename lands: the file is now exactly where it belongs, so record the exact
        // bytes we just wrote — the same buffer already in hand, never a re-read of the file (which would
        // risk capturing a concurrent writer's content). Recording before the rename would risk a "backup
        // of a save that never happened" if the rename then failed. The record is best-effort and silent:
        // BackupStore.Record catches, logs once, and swallows every failure, so a backup problem can never
        // break the save that already succeeded above (data-backup conventions).
        //
        // record: config.json (durable user settings) and paths.json (the user's tracked path list — the
        // externally-linked locations whose loss would strand their work) both flow through here, so both
        // are captured on every real save. This is the ONLY managed-text write site in the app.
        BackupStore.Record(_filePath, bytes);
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
    /// stamp is <see cref="FileTimestamp.FileStamp"/>, the <c>yyyyMMdd-HHmmss-fff-utc</c> machine-paced
    /// filename form the session log also uses, so the fleet has one timestamp formatter for machine-paced
    /// names rather than several; internal so the shape is directly unit-testable without touching disk.
    /// </summary>
    internal static string QuarantinePath(string targetPath, DateTimeOffset timestamp) =>
        Path.Combine(
            Path.GetDirectoryName(targetPath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(targetPath)}-{FileTimestamp.FileStamp(timestamp)}.invalid");
}
