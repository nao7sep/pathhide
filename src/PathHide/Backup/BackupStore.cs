using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PathHide.Services;
using PathHide.Storage;

namespace PathHide.Backup;

/// <summary>
/// The data-backup history (data-backup conventions). It owns one SQLite file, <c>backups.sqlite3</c>,
/// directly under PathHide's storage root (<c>PATHHIDE_DATA_DIR</c> or <c>~/.pathhide</c>, resolved in one
/// place by <see cref="StorageRoot"/> — never a hardcoded path). Each managed <em>text</em> save hands the
/// exact bytes it just wrote here, strictly AFTER its atomic rename lands (see <see cref="JsonStore{T}"/>),
/// and the history keeps the last version of each file per session (one process launch). There is no
/// startup scan, no periodic pass, no restore path: restore is manual.
/// </summary>
/// <remarks>
/// <para>SQLite binding: <c>Microsoft.Data.Sqlite</c>, the ADO.NET provider that ships with SQLitePCLRaw's
/// bundled native library. It needs no separate native rebuild and no packaging churn on a runtime bump.
/// A BLOB round-trips as a <c>byte[]</c>, stored and read back byte-identically for hashing and
/// comparison.</para>
///
/// <para>Recording is best effort, including latency. <see cref="Record"/> only queues: one writer task at
/// a time owns the connection and applies pending writes on its own thread, so a locked, slow or broken
/// store can neither delay nor fail the save that already landed, and a failure is logged and
/// swallowed. Pending writes are kept per path, newest only: a session keeps one row per path, so an
/// intermediate version would be replaced anyway, and the pending set stays as small as the protected
/// files. A crash or forced exit may omit the session's latest version while the file itself stays
/// saved.</para>
///
/// <para>A successful record logs nothing; a line per save would flood the log.</para>
/// </remarks>
public static class BackupStore
{
    // Serialized ISO-8601 with exactly three fractional digits and a Z suffix (2026-07-06T04:05:12.345Z) —
    // the timestamp conventions' stored-value form, NEVER the filename stamp.

    /// <summary>The one table. <c>content</c> is a BLOB of the exact bytes written — never decoded text, so
    /// CR/LF, a BOM, and non-UTF-8 bytes are stored byte-identically. <c>session_id</c> is null for rows
    /// recorded before sessions. The <c>(path, id)</c> index serves the latest-row lookup; the unique
    /// <c>(path, session_id)</c> index is created by <see cref="EnsureSessions"/>, after a store from
    /// before sessions has gained the column.</summary>
    private const string Schema = @"
CREATE TABLE IF NOT EXISTS backups (
  id             INTEGER PRIMARY KEY,
  session_id     TEXT,
  path           TEXT NOT NULL,
  content        BLOB NOT NULL,
  content_sha256 TEXT NOT NULL,
  byte_size      INTEGER NOT NULL,
  written_at_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_backups_path_id ON backups (path, id);
";

    private readonly record struct PendingWrite(byte[] Bytes, DateTimeOffset WrittenAt);

    // Guards the pending writes and whether a writer task is running; never held during I/O.
    private static readonly object PendingGate = new();
    private static readonly Dictionary<string, PendingWrite> Pending = new(StringComparer.Ordinal);
    private static bool _writing;
    private static Task _writer = Task.CompletedTask;

    // Touched only by the writer task (and by Close once it has waited for that task). A null connection
    // after initialization means recording is disabled for this session because the store could not be
    // opened — a single warn was already logged; later writes are dropped rather than retrying (and
    // re-logging) a broken open on every save.
    private static SqliteConnection? _connection;
    private static bool _initialized;

    /// <summary>This launch's session, the one <see cref="Log"/> stamps every record with, so a backup row
    /// and the launch's Records entries name the same session. Settable for tests.</summary>
    internal static string Session { get; set; } = Log.Session;

    /// <summary>The store file under the resolved storage root. Computed lazily (not frozen into a static
    /// field at type-load) so <c>PATHHIDE_DATA_DIR</c> is read after the environment is set, per the
    /// storage-path conventions' caution against import-time resolution — and so a test that relocates the
    /// root sees the new location.</summary>
    private static string StoreFile() => Path.Combine(StorageRoot.Directory, "backups.sqlite3");

    /// <summary>
    /// Queue one managed-text write: <paramref name="absolutePath"/> is the FULL absolute path of the file
    /// as written; <paramref name="bytes"/> is the exact raw bytes just written (the caller already holds
    /// them — never re-read the file). Returns at once; never throws.
    /// </summary>
    public static void Record(string absolutePath, byte[] bytes)
    {
        var write = new PendingWrite(bytes, DateTimeOffset.UtcNow);
        lock (PendingGate)
        {
            Pending[absolutePath] = write;
            if (_writing)
                return;
            _writing = true;
            _writer = Task.Run(WritePending);
        }
    }

    /// <summary>
    /// Waits up to <paramref name="bound"/> for the pending writes, for an ordinary quit. Whatever is still
    /// pending at the bound is left to the exiting process, with one warning. Never throws.
    /// </summary>
    public static async Task FlushAsync(TimeSpan bound, TimeProvider clock)
    {
        Task writer;
        lock (PendingGate)
            writer = _writer;
        try
        {
            await writer.WaitAsync(bound > TimeSpan.Zero ? bound : TimeSpan.Zero, clock).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn("backup store: pending history not recorded before quit");
        }
    }

    private static void WritePending()
    {
        while (true)
        {
            string path;
            PendingWrite write;
            lock (PendingGate)
            {
                if (Pending.Count == 0)
                {
                    _writing = false;
                    return;
                }
                (path, write) = Pending.First();
                Pending.Remove(path);
            }
            Write(path, write);
        }
    }

    /// <summary>
    /// Records one write as this session's row for <paramref name="path"/>: inserted by the session's first
    /// save of the path, replaced by later ones. Content equal to the path's latest row writes nothing, so a
    /// session whose first save repeats an earlier session's last version adds no row. Rows of earlier
    /// sessions are never changed.
    /// </summary>
    private static void Write(string path, PendingWrite write)
    {
        try
        {
            var connection = EnsureOpen();
            if (connection is null)
                return; // open failed earlier; disabled for the session (already warned once)

            var hash = Sha256(write.Bytes);
            using var transaction = connection.BeginTransaction();

            using (var latest = connection.CreateCommand())
            {
                latest.Transaction = transaction;
                latest.CommandText =
                    "SELECT content_sha256 FROM backups WHERE path = $path ORDER BY id DESC LIMIT 1";
                latest.Parameters.AddWithValue("$path", path);
                if (latest.ExecuteScalar() is string latestHash &&
                    string.Equals(latestHash, hash, StringComparison.Ordinal))
                {
                    transaction.Commit();
                    return;
                }
            }

            using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText =
                    "INSERT INTO backups (session_id, path, content, content_sha256, byte_size, written_at_utc) " +
                    "VALUES ($session, $path, $content, $hash, $size, $writtenAt) " +
                    "ON CONFLICT (path, session_id) DO UPDATE SET content = excluded.content, " +
                    "content_sha256 = excluded.content_sha256, byte_size = excluded.byte_size, " +
                    "written_at_utc = excluded.written_at_utc";
                upsert.Parameters.AddWithValue("$session", Session);
                upsert.Parameters.AddWithValue("$path", path);
                upsert.Parameters.AddWithValue("$content", write.Bytes);
                upsert.Parameters.AddWithValue("$hash", hash);
                upsert.Parameters.AddWithValue("$size", write.Bytes.LongLength);
                upsert.Parameters.AddWithValue("$writtenAt", FileTimestamp.SerializedStamp(write.WrittenAt));
                upsert.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            Log.Warn("backup store: failed to record a managed write", ex, new { file = path });
        }
    }

    /// <summary>
    /// Open and initialize the store once: create it if absent, switch on WAL, and give a store from before
    /// sessions its <c>session_id</c> column. On any failure it logs ONE warn, leaves recording disabled for
    /// the session, and never throws.
    /// </summary>
    private static SqliteConnection? EnsureOpen()
    {
        if (_initialized)
            return _connection;
        _initialized = true;

        // Resolved once, before the try, so the catch below can name it without re-running a
        // resolution that may itself throw.
        var storeFile = "(unresolved)";
        SqliteConnection? connection = null;
        try
        {
            var file = StoreFile();
            storeFile = file;
            // not recorded: backups.sqlite3 is the store itself — binary, and written by this backup layer,
            // not through the managed-text atomic-write path — so it never records itself.
            // The first writer under the root does the mkdir -p (storage-path conventions); the store may be
            // the first thing written on a fresh root.
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            connection = FormatVersions.OpenDatabase(file, FormatVersions.Backups, Schema);
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode = WAL;";
                pragma.ExecuteNonQuery();
            }
            EnsureSessions(connection);

            _connection = connection;
        }
        catch (Exception ex)
        {
            // `file` is the path captured before the attempt, never a fresh StoreFile() call:
            // re-resolving inside the catch could throw again (an unresolvable home, an empty
            // PATHHIDE_DATA_DIR) and that second throw would escape EnsureOpen entirely.
            Log.Warn("backup store: could not open; recording disabled for this session", ex,
                new { file = storeFile });
            try { connection?.Dispose(); }
            catch (Exception cleanup) { Log.Warn("backup store: failed to close after open failure", cleanup); }
            _connection = null;
        }

        return _connection;
    }

    /// <summary>
    /// Adds the nullable <c>session_id</c> column to a store from before sessions, keeping its rows as
    /// earlier history, and the unique key that owns one row per path per session. The format version
    /// stays 1: an older build's inserts leave the column null, which never conflicts, so it keeps
    /// recording into the same store (store-recovery-conventions).
    /// </summary>
    private static void EnsureSessions(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText = "SELECT EXISTS (SELECT 1 FROM pragma_table_info('backups') WHERE name = 'session_id')";
            if ((long)probe.ExecuteScalar()! == 0)
            {
                using var add = connection.CreateCommand();
                add.Transaction = transaction;
                add.CommandText = "ALTER TABLE backups ADD COLUMN session_id TEXT";
                add.ExecuteNonQuery();
            }
        }
        using (var index = connection.CreateCommand())
        {
            index.Transaction = transaction;
            index.CommandText =
                "CREATE UNIQUE INDEX IF NOT EXISTS idx_backups_path_session ON backups (path, session_id)";
            index.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>SHA-256 of the exact bytes, lowercase hex.</summary>
    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Waits for pending writes, then closes the store. For tests that need to release the file
    /// handle between throwaway roots; the app itself lets the process exit close it. Resets the store so
    /// the next <see cref="Record"/> re-opens against the current <c>PATHHIDE_DATA_DIR</c>.</summary>
    public static void Close()
    {
        Task writer;
        lock (PendingGate)
            writer = _writer;
        writer.GetAwaiter().GetResult();

        try
        {
            _connection?.Close();
            _connection?.Dispose();
        }
        catch (Exception ex)
        {
            // Best-effort: a close failure on teardown is harmless, but it is still a failure that was
            // recovered from, so it gets a line rather than silence.
            Log.Warn("backup store: close failed", ex);
        }

        _connection = null;
        _initialized = false;
    }
}
