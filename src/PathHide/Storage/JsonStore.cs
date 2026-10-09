using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PathHide.Backup;
using PathHide.Services;

namespace PathHide.Storage;

/// <summary>
/// Generic JSON-backed store with atomic replace (write-to-temp-then-rename). A missing file yields the
/// type's default-constructed value. What a present file that cannot be used yields depends on what the
/// store holds (store-recovery-conventions):
/// <list type="bullet">
///   <item>A store holding the user's work product (<c>haltWhenUnreadable</c>) is left exactly in place:
///   <see cref="Load"/> and <see cref="Save"/> throw <see cref="UnreadableStoreException"/> or
///   <see cref="NewerFormatException"/>, so an unreadable or newer file is never written over.</item>
///   <item>Any other store holds only harmless preferences or presentation state: a file that cannot be
///   read, is invalid or records a newer version yields defaults with a warning in the log and stays
///   untouched, and the next save replaces it. Nothing is moved aside.</item>
/// </list>
/// </summary>
/// <remarks>
/// The file is a JSON object: <typeparamref name="T"/>'s properties beside the format version this
/// store owns (store-recovery-conventions).
/// </remarks>
/// <remarks>
/// The app's single managed-text atomic-write choke point, and so the one place the data-backup hook
/// lives: <see cref="WriteAtomically"/> hands the exact bytes it just wrote to <see cref="BackupStore"/>
/// strictly AFTER the rename lands. A managed-text write that bypasses this store is a silent backup gap.
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
    private readonly bool _haltWhenUnreadable;

    /// <summary>
    /// Creates a store rooted at <see cref="StorageRoot.Directory"/>.
    /// </summary>
    /// <param name="fileName">File name (no directory component), e.g. <c>"paths.json"</c>.</param>
    /// <param name="label">Human-readable noun used in log messages, e.g. <c>"paths"</c>.</param>
    /// <param name="formatVersion">The file's format version, from <see cref="FormatVersions"/>.</param>
    /// <param name="recordBackup">False for a store that is volatile state and nothing else (window
    /// placement): its saves are written atomically but not recorded into the backup history.</param>
    /// <param name="haltWhenUnreadable">True for a store holding the user's work product: a file present
    /// but unreadable or newer is left exactly in place, and <see cref="Load"/> and <see cref="Save"/>
    /// throw instead of falling back to defaults or writing over it.</param>
    public JsonStore(string fileName, string label, int formatVersion, bool recordBackup = true, bool haltWhenUnreadable = false)
    {
        _filePath = Path.Combine(StorageRoot.Directory, fileName);
        _label = label;
        _formatVersion = formatVersion;
        _recordBackup = recordBackup;
        _haltWhenUnreadable = haltWhenUnreadable;
    }

    public LoadedStore<T> Load()
    {
        // An absent file is normal (first run): not a failure, so it is not logged as one.
        if (!File.Exists(_filePath))
        {
            Log.Info("store: no existing data, using defaults", new { label = _label });
            return new LoadedStore<T>(new T(), WasUnreadable: false);
        }

        try
        {
            var value = ReadFile();
            Log.Info("store: loaded", new { label = _label, path = _filePath });
            return new LoadedStore<T>(value, WasUnreadable: false);
        }
        catch (Exception ex) when (!_haltWhenUnreadable)
        {
            // The file stays exactly as it is until the next save replaces it; this session runs on
            // defaults. Absent and unreadable are reported apart: unreadable means there was content.
            Log.Warn("store: file unusable, using defaults; left in place until the next save", ex,
                new { label = _label, path = _filePath });
            return new LoadedStore<T>(new T(), WasUnreadable: true);
        }
    }

    public void Save(T value)
    {
        try
        {
            StorageRoot.EnsureExists();
            // A store that halts when unreadable reads the file it would replace, so it refuses an
            // unreadable or newer one: after a failed Reload of a hand-edited list, a save must not
            // overwrite the user's edit. Other stores replace whatever is there.
            if (_haltWhenUnreadable && File.Exists(_filePath))
                ReadFile();
            var document = JsonSerializer.SerializeToNode(value, JsonOptions.Default) as JsonObject
                ?? throw new InvalidOperationException($"The {_label} store holds a JSON object.");
            document.Insert(0, FormatVersions.JsonKey, _formatVersion);
            var json = document.ToJsonString(JsonOptions.Default);
            // Encode once, here, so the exact bytes written to disk are the exact bytes recorded to the
            // backup store after the rename (no re-encode, no re-read). No BOM: File.WriteAllText/Encoding
            // .UTF8 without a preamble matches what the app writes and reads back.
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
            if (File.Exists(_filePath) && File.ReadAllBytes(_filePath).AsSpan().SequenceEqual(bytes))
                return;
            WriteAtomically(bytes);
            Log.Info("store: saved", new { label = _label, path = _filePath });
        }
        catch (Exception ex)
        {
            Log.Error("store: save failed", ex, new { label = _label, path = _filePath });
            throw;
        }
    }

    /// <summary>
    /// Reads the present file. For a store that halts, every failure surfaces as
    /// <see cref="NewerFormatException"/> or <see cref="UnreadableStoreException"/> naming the file.
    /// </summary>
    private T ReadFile()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_filePath));
            var version = FormatVersions.Recorded(document.RootElement)
                ?? throw new JsonException($"The {_label} file is not an object with a valid {FormatVersions.JsonKey}.");
            if (version > _formatVersion)
                throw new NewerFormatException(_filePath, version, _formatVersion);

            return document.RootElement.Deserialize<T>(JsonOptions.Default)
                ?? throw new JsonException($"The {_label} file could not be read as its document.");
        }
        catch (NewerFormatException newer) when (_haltWhenUnreadable)
        {
            Log.Warn("store: written by a newer version, left as it is",
                new { label = _label, path = _filePath, version = newer.Version, supported = _formatVersion });
            throw;
        }
        catch (Exception ex) when (_haltWhenUnreadable)
        {
            Log.Warn("store: file unreadable, left in place", ex, new { label = _label, path = _filePath });
            throw new UnreadableStoreException(_label, _filePath, ex);
        }
    }

    private void WriteAtomically(byte[] bytes)
    {
        // The BCL's random name, its dot removed so the temp keeps one role extension.
        var tempPath = TempPath(_filePath, Path.GetRandomFileName().Replace(".", "", StringComparison.Ordinal));

        try
        {
            // not recorded: this temp is atomic-write scratch under the derived-filename grammar, never a
            // managed-text destination — it is renamed away (or deleted) before anything reads it, and the
            // record fires only on the final file below. It is written directly, not through this store.
            File.WriteAllBytes(tempPath, bytes);

            // A pure atomic temp-then-rename with no .bak sidecar: replace the existing file in place, or
            // move the temp into a fresh one. This is the durability floor (the storage-path conventions);
            // point-in-time history lives in the backup store, not a last-good copy beside
            // the file.
            if (File.Exists(_filePath))
            {
                // Keep ordinary permission bits through the runtime (content-lifecycle-conventions).
                if (OperatingSystem.IsMacOS())
                    File.SetUnixFileMode(tempPath, File.GetUnixFileMode(_filePath));
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

        // Strictly AFTER the rename lands: the save is complete here. Hand over the exact bytes just
        // written — the same buffer already in hand, never a re-read of the file (which would risk
        // capturing another writer's content). Handing over before the rename would risk a "backup of a
        // save that never happened" if the rename then failed. The hand-over only queues: the backup
        // writer records on its own thread, so a slow or failing history can neither delay nor fail this
        // save (data-backup conventions).
        //
        // record: config.json (durable user settings) and paths.json (the user's tracked path list — the
        // externally-linked locations whose loss would strand their work) are captured at every real
        // save, keeping the session's last version of each. state.json (window geometry) is volatile
        // state and opts out via recordBackup: false.
        // This is the ONLY managed-text write site in the app.
        if (_recordBackup)
            BackupStore.Record(_filePath, bytes);
    }

    /// <summary>
    /// The atomic-write temp path for <paramref name="targetPath"/>: the target's stem plus
    /// <paramref name="discriminator"/>, one role extension (<c>.tmp</c>), in the same directory as the
    /// target — the derived-filename grammar, never a dot-appended suffix (e.g. never
    /// <c>config.json.&lt;x&gt;.tmp</c>). The discriminator is a random name without dots (see
    /// <see cref="WriteAtomically"/>); internal so the shape is directly unit-testable without
    /// touching disk.
    /// </summary>
    internal static string TempPath(string targetPath, string discriminator) =>
        Path.Combine(
            Path.GetDirectoryName(targetPath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(targetPath)}-{discriminator}.tmp");
}
