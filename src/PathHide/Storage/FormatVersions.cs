using System;
using System.IO;
using PathHide.Services;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PathHide.Storage;

/// <summary>
/// The format version of every store PathHide writes, one number per format, and how each kind of store
/// records it (store-recovery-conventions).
/// </summary>
public static class FormatVersions
{
    /// <summary><c>paths.json</c>.</summary>
    public const int PathList = 1;

    /// <summary><c>config.json</c>.</summary>
    public const int Settings = 1;

    /// <summary><c>state.json</c>.</summary>
    public const int State = 1;

    /// <summary><c>records.sqlite3</c>.</summary>
    public const int Records = 1;

    /// <summary><c>backups.sqlite3</c>.</summary>
    public const int Backups = 1;

    /// <summary>The top-level key a JSON store records its version under.</summary>
    internal const string JsonKey = "formatVersion";

    /// <summary>
    /// The version a JSON store's root records, or null when the root is not an object or does not record
    /// a positive integer: a store without its marker is unreadable.
    /// </summary>
    internal static int? Recorded(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(JsonKey, out var marker)
        && marker.ValueKind == JsonValueKind.Number
        && marker.TryGetInt32(out var version)
        && version >= 1
            ? version
            : null;

    /// <summary>
    /// Refuses a database whose <c>PRAGMA user_version</c> records no version (0) or one newer than
    /// <paramref name="supported"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The database records no version.</exception>
    /// <exception cref="NewerFormatException">The database is newer than <paramref name="supported"/>.</exception>
    internal static void CheckDatabase(SqliteConnection connection, string path, int supported)
    {
        var recorded = UserVersion(connection);
        if (recorded < 1)
            throw new InvalidDataException($"{path} records no format version.");
        if (recorded > supported)
            throw new NewerFormatException(path, (int)recorded, supported);
    }

    /// <summary>
    /// Opens the database at <paramref name="path"/>, creating it when absent, with one marker check. A
    /// database holding no schema objects is new: it receives <paramref name="schema"/> and its version
    /// in one transaction, so a failed initialization leaves an empty file that the next open
    /// initializes again. Any other database must record a version this build reads; its
    /// <paramref name="schema"/> statements then run as written, so they must be idempotent.
    /// </summary>
    /// <remarks>
    /// One GUI per data root is the only writer of a store (<see cref="Services.SingleInstanceLease"/>),
    /// so creation needs no claim against a concurrent creator.
    /// </remarks>
    internal static SqliteConnection OpenDatabase(string path, int supported, string schema)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA busy_timeout = 5000;";
                command.ExecuteNonQuery();
            }
            var isNew = UserVersion(connection) == 0 && !HasSchema(connection);
            if (!isNew)
                CheckDatabase(connection, path, supported);
            using (var transaction = connection.BeginTransaction())
            {
                using var initialize = connection.CreateCommand();
                initialize.Transaction = transaction;
                initialize.CommandText = isNew ? schema + $"PRAGMA user_version = {supported};" : schema;
                initialize.ExecuteNonQuery();
                transaction.Commit();
            }
            return connection;
        }
        catch
        {
            try { connection.Dispose(); }
            catch (Exception cleanup) { Log.Warn("database: failed to close after open failure", cleanup, new { path }); }
            throw;
        }
    }

    private static bool HasSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master);";
        return (long)command.ExecuteScalar()! != 0;
    }

    private static long UserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }
}
