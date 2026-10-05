using System.IO;
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
    /// Stamps a brand-new database, one holding nothing yet, with <paramref name="supported"/>; any other
    /// database goes through <see cref="CheckDatabase"/>. Runs before anything else writes to it.
    /// </summary>
    internal static void AdoptDatabase(SqliteConnection connection, string path, int supported)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
        if (UserVersion(connection) == 0 && (long)command.ExecuteScalar()! == 0)
        {
            command.CommandText = $"PRAGMA user_version = {supported};";
            command.ExecuteNonQuery();
            return;
        }

        CheckDatabase(connection, path, supported);
    }

    private static long UserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }
}
