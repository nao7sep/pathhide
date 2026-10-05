using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PathHide.Backup;
using PathHide.Services;

namespace PathHide.Storage;

/// <summary>
/// Generic JSON-backed store with atomic replace (write-to-temp-then-rename).
/// A missing file yields the type's default-constructed value; a present but
/// unparseable file is quarantined aside, bytes preserved, before the default
/// is returned (storage-path conventions).
/// </summary>
/// <remarks>
/// The file is a JSON object: <typeparamref name="T"/>'s properties beside the format version this
/// store owns (store-recovery-conventions). A file recording a newer version is neither read,
/// quarantined nor written: <see cref="Load"/> and <see cref="Save"/> throw
/// <see cref="NewerFormatException"/> and leave it as it is.
/// </remarks>
/// <remarks>
/// The app's single managed-text atomic-write choke point, and so the one place
/// the data-backup hook lives: <see cref="WriteAtomically"/> records the exact
/// bytes it just wrote into <see cref="BackupStore"/> strictly AFTER the rename
/// lands. A managed-text write that bypasses this store is a silent backup gap.
/// </remarks>
/// <remarks>
/// The store imposes no ordering on the value it receives. If on-disk ordering
/// matters (diff stability, hand-editing), the caller sorts a copy before
/// <see cref="Save"/>.
/// </remarks>
public sealed class JsonStore<T> : IJsonStore<T> where T : class, new()
{
    private readonly string _filePath;
    private readonly string _label;
    private readonly int _formatVersion;
    private readonly bool _recordBackup;

    /// <summary>
    /// Creates a store rooted at <see cref="StorageRoot.Directory"/>.
    /// </summary>
    /// <param name="fileName">File name (no directory component), e.g. <c>"paths.json"</c>.</param>
    /// <param name="label">Human-readable noun used in log messages, e.g. <c>"paths"</c>.</param>
    /// <param name="formatVersion">The file's format version, from <see cref="FormatVersions"/>.</param>
    /// <param name="recordBackup">False for a store that is volatile state and nothing else (window
    /// placement): its saves are written atomically but not recorded into the backup history.</param>
    public JsonStore(string fileName, string label, int formatVersion, bool recordBackup = true)
    {
        _filePath = Path.Combine(StorageRoot.Directory, fileName);
        _label = label;
        _formatVersion = formatVersion;
        _recordBackup = recordBackup;
    }

    public LoadedStore<T> Load()
    {
        if (TryLoadFile(out var value, out var wasUnreadable))
            return new LoadedStore<T>(value, WasUnreadable: false);

        // Reached on first run (no file yet — normal) or after the live file was present but
        // unreadable (already quarantined and logged a warn above). There is no .bak fallback: a
        // live file that will not parse is moved aside rather than reset over, and its earlier
        // content is recovered, if ever needed, from the quarantined file itself or the
        // write-through backup store backups.sqlite3 (see the data-backup conventions).
        //
        // The two are reported apart, because they mean opposite things to the
        // caller: absent is a first run, unreadable means the user had content
        // that could not be read.
        if (!wasUnreadable)
            Log.Info("store: no existing data, using defaults", new { label = _label });

        return new LoadedStore<T>(new T(), wasUnreadable);
    }

    public void Save(T value)
    {
        try
        {
            StorageRoot.EnsureExists();
            RefuseNewerFile();
            var document = JsonSerializer.SerializeToNode(value, JsonOptions.Default) as JsonObject
                ?? throw new InvalidOperationException($"The {_label} store holds a JSON object.");
            document.Insert(0, FormatVersions.JsonKey, _formatVersion);
            var json = document.ToJsonString(JsonOptions.Default);
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

    private bool TryLoadFile(out T value, out bool wasUnreadable)
    {
        value = new T();
        wasUnreadable = false;

        // An absent file is normal (first run): not a failure, so it is not logged here — the
        // caller decides what the absence means.
        if (!File.Exists(_filePath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_filePath));
            var version = FormatVersions.Recorded(document.RootElement)
                ?? throw new JsonException($"The {_label} file is not an object with a valid {FormatVersions.JsonKey}.");
            if (version > _formatVersion)
                throw Newer(version);

            value = document.RootElement.Deserialize<T>(JsonOptions.Default)
                ?? throw new JsonException($"The {_label} file could not be read as its document.");
            Log.Info("store: loaded", new { label = _label, path = _filePath });
            return true;
        }
        catch (Exception ex) when (ex is not NewerFormatException)
        {
            // Present but unparseable: quarantine aside (bytes preserved) before the caller
            // decides what to do; only a later user change recreates the file through Save.
            Quarantine(ex);
            wasUnreadable = true;
            return false;
        }
    }

    /// <summary>
    /// Refuses to write over a file that records a newer version than this store's. A file that cannot be
    /// parsed records none this build can see, and is written over as before.
    /// </summary>
    private void RefuseNewerFile()
    {
        if (!File.Exists(_filePath))
            return;

        int? recorded;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_filePath));
            recorded = FormatVersions.Recorded(document.RootElement);
        }
        catch (JsonException)
        {
            return;
        }

        if (recorded > _formatVersion)
            throw Newer(recorded.Value);
    }

    private NewerFormatException Newer(int version)
    {
        Log.Warn("store: written by a newer version, left as it is",
            new { label = _label, path = _filePath, version, supported = _formatVersion });
        return new NewerFormatException(_filePath, version, _formatVersion);
    }

    /// <summary>
    /// Moves the unparseable live file aside to its timestamped <c>.invalid</c> quarantine name,
    /// preserving its bytes, and logs one warning naming both paths. The move either lands or its
    /// failure propagates — swallowing it would leave the corrupt file in place for the caller's
    /// reset to overwrite. The composition root catches the propagation and reports a startup halt.
    /// </summary>
    private void Quarantine(Exception ex)
    {
        var quarantinePath = QuarantinePath(_filePath, DateTimeOffset.UtcNow);

        try
        {
            // not recorded: a move-aside of an already-unreadable file, not a managed-text write.
            // A later user change through WriteAtomically records its saved content.
            File.Move(_filePath, quarantinePath);
        }
        catch (Exception moveEx)
        {
            Log.Warn("store: file unreadable; quarantine move failed", moveEx,
                new { label = _label, path = _filePath, quarantinePath, readError = ex.Message });
            throw;
        }

        Log.Warn("store: file unreadable, quarantined", ex,
            new { label = _label, path = _filePath, quarantinePath });
        QuarantineJournal.Record(_label, quarantinePath);
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
        // externally-linked locations whose loss would strand their work) are captured on every real
        // save. state.json (window geometry) is volatile state and opts out via recordBackup: false.
        // This is the ONLY managed-text write site in the app.
        if (_recordBackup)
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
    /// filename form a session's fallback log file also uses, so the fleet has one timestamp formatter for
    /// machine-paced names rather than several; internal so the shape is directly unit-testable without
    /// touching disk.
    /// </summary>
    internal static string QuarantinePath(string targetPath, DateTimeOffset timestamp) =>
        Path.Combine(
            Path.GetDirectoryName(targetPath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(targetPath)}-{FileTimestamp.FileStamp(timestamp)}.invalid");
}
