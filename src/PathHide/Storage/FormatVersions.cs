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

    /// <summary>The version a store that records none reads as.</summary>
    private const int Unrecorded = 1;

    /// <summary>The top-level key a JSON store records its version under.</summary>
    internal const string JsonKey = "formatVersion";

    /// <summary>
    /// The version a JSON store's root records: <see cref="Unrecorded"/> when it records none, and null
    /// when the root is not an object or its marker is not a positive integer, a shape this build cannot read.
    /// </summary>
    internal static int? Recorded(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!root.TryGetProperty(JsonKey, out var marker))
            return Unrecorded;
        return marker.ValueKind == JsonValueKind.Number && marker.TryGetInt32(out var version) && version >= 1
            ? version
            : null;
    }

    /// <summary>
    /// The version a database records in <c>PRAGMA user_version</c>, refused when newer than
    /// <paramref name="supported"/>. A database that records none holds 0 there.
    /// </summary>
    /// <exception cref="NewerFormatException">The database is newer than <paramref name="supported"/>.</exception>
    internal static void CheckDatabase(SqliteConnection connection, string path, int supported) =>
        CheckDatabase(UserVersion(connection), path, supported);

    /// <summary>
    /// <see cref="CheckDatabase(SqliteConnection, string, int)"/> before anything writes to the database,
    /// then records the version a database that records none reads as.
    /// </summary>
    internal static void AdoptDatabase(SqliteConnection connection, string path, int supported)
    {
        var recorded = UserVersion(connection);
        CheckDatabase(recorded, path, supported);
        if (recorded != 0)
            return;

        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {Unrecorded};";
        command.ExecuteNonQuery();
    }

    private static void CheckDatabase(long recorded, string path, int supported)
    {
        if (recorded < 0)
            throw new InvalidDataException($"{path} records format version {recorded}.");
        var version = recorded == 0 ? Unrecorded : (int)recorded;
        if (version > supported)
            throw new NewerFormatException(path, version, supported);
    }

    private static long UserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }
}
