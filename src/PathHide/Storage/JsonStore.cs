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
///   <item>A store holding valuable content, the path list or settings (<c>haltWhenUnreadable</c>), is left exactly in place:
///   <see cref="Load"/> and <see cref="Save"/> throw <see cref="UnreadableStoreException"/> or
///   <see cref="NewerFormatException"/>, so an unreadable or newer file is never written over.</item>
///   <item>Any other store holds only disposable presentation state: a file that cannot be read or is
///   invalid yields defaults with a warning in the log, and the next save replaces it.</item>
///   <item>A newer file is refused by every store: <see cref="Load"/> and <see cref="Save"/> throw
///   <see cref="NewerFormatException"/>, and it is never written over. Nothing is ever moved aside.</item>
/// </list>
/// </summary>
/// <remarks>
/// The file is a JSON object: the identity header this store's format owns (<see cref="FileFormats"/>),
/// then <typeparamref name="T"/>'s properties.
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
    private readonly FileFormat _format;
    private readonly bool _recordBackup;
    private readonly bool _haltWhenUnreadable;

    internal Action<string> DeleteScratch { get; init; } = File.Delete;

    /// <summary>
    /// Creates a store rooted at <see cref="StorageRoot.Directory"/>.
    /// </summary>
    /// <param name="fileName">File name (no directory component), e.g. <c>"paths.json"</c>.</param>
    /// <param name="label">Human-readable noun used in log messages, e.g. <c>"paths"</c>.</param>
    /// <param name="format">The file's format, from <see cref="FileFormats"/>.</param>
    /// <param name="recordBackup">False for a store that is volatile state and nothing else (window
    /// placement): its saves are written atomically but not recorded into the backup history.</param>
    /// <param name="haltWhenUnreadable">True for a store holding valuable content: a file present
    /// but unreadable or newer is left exactly in place, and <see cref="Load"/> and <see cref="Save"/>
    /// throw instead of falling back to defaults or writing over it.</param>
    public JsonStore(string fileName, string label, FileFormat format, bool recordBackup = true, bool haltWhenUnreadable = false)
    {
        _filePath = Path.Combine(StorageRoot.Directory, fileName);
        _label = label;
        _format = format;
        _recordBackup = recordBackup;
        _haltWhenUnreadable = haltWhenUnreadable;
    }

    public LoadedStore<T> Load()
    {
        try
        {
            // An absent file is normal (first run): not a failure, so it is not logged as one.
            if (ReadFile(out _) is not { } value)
            {
                Log.Info("store: no existing data, using defaults", new { label = _label });
                return new LoadedStore<T>(new T(), WasUnreadable: false);
            }
            Log.Info("store: loaded", new { label = _label, path = _filePath });
            return new LoadedStore<T>(value, WasUnreadable: false);
        }
        catch (Exception ex) when (!_haltWhenUnreadable && ex is not NewerFormatException)
        {
            // Only disposable state falls back; a newer file stops the launch, and Save refuses it too.
            Log.Warn("store: file unusable, using session defaults", ex,
                new { label = _label, path = _filePath });
            return new LoadedStore<T>(new T(), WasUnreadable: true);
        }
    }

    public void Save(T value) => Save(value, compareExistingContent: true);

    // Settings compares its in-memory sets; on-disk admission still protects newer files.
    internal void Save(T value, bool compareExistingContent)
    {
        byte[]? existing;
        try { ReadFile(out existing); }
        catch (Exception ex) when (!_haltWhenUnreadable && ex is not NewerFormatException)
        {
            existing = null; // Invalid disposable preferences can still be replaced.
        }
        var document = JsonSerializer.SerializeToNode(value, JsonOptions.Default) as JsonObject
            ?? throw new InvalidOperationException($"The {_label} store holds a JSON object.");
        FileFormats.WriteHeader(document, _format);
        var bytes = new UTF8Encoding(false).GetBytes(document.ToJsonString(JsonOptions.Default));
        if (compareExistingContent && existing is not null && existing.AsSpan().SequenceEqual(bytes))
            return;
        StorageRoot.EnsureExists();
        WriteAtomically(bytes);
        Log.Info("store: saved", new { label = _label, path = _filePath });
    }

    // Only the read itself establishes absence. Reuse these exact admitted bytes for no-op saves.
    private T? ReadFile(out byte[]? bytes)
    {
        bytes = null;
        try
        {
            try { bytes = File.ReadAllBytes(_filePath); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            // A repeated property is ambiguous, not "last one wins": the parser refuses it.
            using var document = JsonDocument.Parse(bytes.AsMemory(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException($"The {_label} file must contain an object.");
            FileFormats.CheckHeader(root, _format, _filePath);
            return FileFormats.Content(root).Deserialize<T>(JsonOptions.Default)
                ?? throw new JsonException($"The {_label} file could not be read as its document.");
        }
        catch (NewerFormatException) { throw; }
        catch (Exception ex) when (_haltWhenUnreadable)
        {
            throw new UnreadableStoreException(_label, _filePath, ex);
        }
    }

    private void WriteAtomically(byte[] bytes)
    {
        var tempPath = TempPath(_filePath, StoreScratch.NewDiscriminator());

        var ownsScratch = false;
        try
        {
            // not recorded: this temp is atomic-write scratch under the derived-filename grammar, never a
            // managed-text destination — it is renamed away (or deleted) before anything reads it, and the
            // record fires only on the final file below. It is written directly, not through this store.
            using (var staged = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsScratch = true;
                staged.Write(bytes);
                staged.Flush(flushToDisk: true);
            }

            // A pure atomic temp-then-rename with no .bak sidecar: replace the existing file in place, or
            // move the temp into a fresh one. This is the durability floor (the storage-path conventions);
            // point-in-time history lives in the backup store, not a last-good copy beside
            // the file.
            if (File.Exists(_filePath))
            {
                // The replacement takes the old file's permission bits, so an atomic save never widens or
                // narrows who can read the user's file.
                if (OperatingSystem.IsMacOS())
                    File.SetUnixFileMode(tempPath, File.GetUnixFileMode(_filePath));
                File.Replace(tempPath, _filePath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
        catch
        {
            // Successful rename already consumed the scratch file: no post-publication I/O.
            // Failed-write cleanup is best-effort and must preserve the primary failure.
            try { if (ownsScratch) DeleteScratch(tempPath); }
            catch (Exception ex) { Log.Warn("store: could not remove failed-write scratch", ex, new { tempPath }); }
            throw;
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

/// <summary>
/// The scratch files <see cref="JsonStore{T}"/> saves write beside their target before renaming them
/// into place, and their removal when a save was cut off between the two.
/// </summary>
internal static class StoreScratch
{
    private static readonly System.Text.RegularExpressions.Regex Name =
        new(@"^[^-]+-[a-z0-9]{11}\.tmp$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The BCL's random name, its dot removed so the scratch file keeps one role extension.</summary>
    internal static string NewDiscriminator() => Path.GetRandomFileName().Replace(".", "", StringComparison.Ordinal);

    /// <summary>
    /// Removes the scratch files in <paramref name="directory"/> that saves left when the app was killed,
    /// crashed or lost power between writing one and renaming it. Called at startup, before any save, by
    /// the one instance that owns the folder, so none of them is a save still under way.
    /// </summary>
    internal static void Sweep(string directory)
    {
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.tmp"))
        {
            if (!Name.IsMatch(Path.GetFileName(path)))
                continue;
            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("store: could not remove scratch left by an interrupted save", ex, new { path });
            }
        }
        if (removed > 0)
            Log.Info("store: removed scratch left by interrupted saves", new { removed });
    }
}
