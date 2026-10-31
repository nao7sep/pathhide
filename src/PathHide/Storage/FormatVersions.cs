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
    /// an integer. A store reads only its own version: a newer one is refused as newer, and anything
    /// else, a missing marker included, as unreadable.
    /// </summary>
    internal static int? Recorded(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(JsonKey, out var marker)
        && marker.ValueKind == JsonValueKind.Number
        && marker.TryGetInt32(out var version)
            ? version
            : null;

    /// <summary>
    /// Opens the database at <paramref name="path"/>, creating it when absent, with one marker check. A
    /// database holding no schema objects is new: it receives <paramref name="schema"/> and its version
    /// in one transaction, so a failed initialization leaves an empty file that the next open
    /// initializes again. Any other database must record <paramref name="supported"/>; its
    /// <paramref name="schema"/> statements then run as written, so they must be idempotent.
    /// </summary>
    /// <remarks>
    /// One GUI per data root is the only writer of a store (<see cref="Services.SingleInstanceLease"/>),
    /// so creation needs no claim against a concurrent creator.
    /// </remarks>
    /// <exception cref="NewerFormatException">The database records a version newer than <paramref name="supported"/>.</exception>
    /// <exception cref="InvalidDataException">The database holds a schema but records another version, or none.</exception>
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
            // No busy timeout of the app's own: each database has one connection in the app, its owner's,
            // and the provider's default retry of a busy command stays as the library sets it.
            connection.Open();
            var recorded = UserVersion(connection);
            var isNew = recorded == 0 && !HasSchema(connection);
            if (recorded > supported)
                throw new NewerFormatException(path, (int)recorded, supported);
            if (!isNew && recorded != supported)
                throw new InvalidDataException($"{path} records format version {recorded}; this build reads only {supported}.");
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
