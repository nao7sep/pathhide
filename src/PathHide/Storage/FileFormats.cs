using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PathHide.Services;

namespace PathHide.Storage;

/// <summary>One of PathHide's file formats: what its identity header names it, and the one version this build reads.</summary>
public sealed record FileFormat(string Kind, int Version);

/// <summary>
/// Every format PathHide writes, and the one place its identity header is built and checked
/// (file-identity-conventions). The header's <c>format</c> says what a file is; its <c>writer</c> says
/// which build wrote it, for people only: it is written on every save and never read.
/// </summary>
/// <remarks>
/// Each store reads only its own version: a newer one is refused as newer and left untouched; a missing
/// or malformed header, another owner or kind, or any other version makes the file unreadable, with no
/// fallback (store-recovery-conventions). Before the first official release the formats stay at 1.
/// </remarks>
public static class FileFormats
{
    /// <summary>The product every PathHide format belongs to; never changes once a file exists.</summary>
    public const string Owner = "pathhide";

    /// <summary><c>paths.json</c>.</summary>
    public static readonly FileFormat PathList = new("pathList", 1);

    /// <summary><c>config.json</c>.</summary>
    public static readonly FileFormat Settings = new("config", 1);

    /// <summary><c>state.json</c>.</summary>
    public static readonly FileFormat State = new("state", 1);

    /// <summary><c>records.sqlite3</c>.</summary>
    public static readonly FileFormat Records = new("records", 1);

    /// <summary><c>backups.sqlite3</c>.</summary>
    public static readonly FileFormat Backups = new("backups", 1);

    internal const string FormatKey = "format";
    internal const string WriterKey = "writer";

    private static readonly AssemblyName Package = typeof(FileFormats).Assembly.GetName();

    /// <summary>The writer's package name, the assembly the build stamps from <c>Directory.Build.props</c>.</summary>
    internal static string WriterName { get; } = Package.Name!;

    /// <summary>The writer's release version, as the About dialog shows it.</summary>
    internal static string WriterVersion { get; } = Package.Version!.ToString(3);

    // --- JSON ---

    /// <summary>Puts the header first in <paramref name="document"/>: <c>format</c>, then <c>writer</c>.</summary>
    internal static void WriteHeader(JsonObject document, FileFormat format)
    {
        document.Insert(0, FormatKey, new JsonObject
        {
            ["owner"] = Owner,
            ["kind"] = format.Kind,
            ["version"] = format.Version,
        });
        document.Insert(1, WriterKey, new JsonObject
        {
            ["name"] = WriterName,
            ["version"] = WriterVersion,
        });
    }

    /// <summary>
    /// Checks the header of a JSON store's <paramref name="root"/> object against <paramref name="expected"/>.
    /// Other keys inside <c>format</c> are ignored; <c>writer</c> is not read.
    /// </summary>
    /// <exception cref="NewerFormatException">The file is this format at a newer version.</exception>
    /// <exception cref="JsonException">The header is missing or malformed, or names another format or version.</exception>
    internal static void CheckHeader(JsonElement root, FileFormat expected, string path)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(FormatKey, out var format)
            || format.ValueKind != JsonValueKind.Object
            || !format.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.String
            || !format.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
            || !format.TryGetProperty("version", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out var version) || version < 1)
            throw new JsonException($"{path} has no valid format header.");
        Check(owner.GetString(), kind.GetString(), version, expected, path);
    }

    /// <summary>A JSON store's content: <paramref name="root"/> without its header keys.</summary>
    internal static JsonObject Content(JsonElement root)
    {
        var content = JsonObject.Create(root)!;
        content.Remove(FormatKey);
        content.Remove(WriterKey);
        return content;
    }

    private static void Check(string? owner, string? kind, int version, FileFormat expected, string path)
    {
        if (owner != Owner || kind != expected.Kind)
            throw new JsonException($"{path} is a {owner}/{kind} file, not {Owner}/{expected.Kind}.");
        if (version > expected.Version)
            throw new NewerFormatException(path, version, expected.Version);
        if (version != expected.Version)
            throw new JsonException($"{path} records format version {version}; this build reads only {expected.Version}.");
    }

    // --- SQLite ---

    private const string HeaderTable = "file_header";

    /// <summary>
    /// Opens the database at <paramref name="path"/> for writing, creating it when absent, with one header
    /// check. A database holding no schema objects is new: it receives <paramref name="schema"/>, the
    /// <c>file_header</c> table and its version in one transaction, so a failed initialization leaves an
    /// empty file that the next open initializes again. Any other database must hold a
    /// <c>file_header</c> naming <paramref name="format"/> and record its version in
    /// <c>user_version</c>; its <paramref name="schema"/> statements then run as written, so they must
    /// be idempotent, and the writer rows are rewritten for this build.
    /// </summary>
    /// <remarks>
    /// One GUI per data root is the only writer of a store (<see cref="Services.SingleInstanceLease"/>),
    /// so creation needs no claim against a concurrent creator.
    /// </remarks>
    /// <exception cref="NewerFormatException">The database is this format at a newer version.</exception>
    /// <exception cref="InvalidDataException">The database holds a schema but no valid header for this format and version.</exception>
    internal static SqliteConnection OpenDatabase(string path, FileFormat format, string schema)
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
            var recorded = Scalar<long>(connection, "PRAGMA user_version;");
            var isNew = recorded == 0 && Scalar<long>(connection, "SELECT EXISTS (SELECT 1 FROM sqlite_master);") == 0;
            if (!isNew)
                CheckDatabaseHeader(connection, recorded, format, path);
            using (var transaction = connection.BeginTransaction())
            {
                using (var initialize = connection.CreateCommand())
                {
                    initialize.Transaction = transaction;
                    initialize.CommandText = isNew
                        ? schema + $"CREATE TABLE {HeaderTable} (key TEXT PRIMARY KEY, value TEXT NOT NULL); PRAGMA user_version = {format.Version};"
                        : schema;
                    initialize.ExecuteNonQuery();
                }
                if (isNew)
                {
                    SetHeaderRow(connection, transaction, "format.owner", Owner);
                    SetHeaderRow(connection, transaction, "format.kind", format.Kind);
                }
                SetHeaderRow(connection, transaction, "writer.name", WriterName);
                SetHeaderRow(connection, transaction, "writer.version", WriterVersion);
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

    private static void CheckDatabaseHeader(SqliteConnection connection, long recorded, FileFormat expected, string path)
    {
        if (Scalar<long>(connection, $"SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '{HeaderTable}');") == 0)
            throw new InvalidDataException($"{path} has no {HeaderTable} table.");
        var owner = HeaderRow(connection, "format.owner");
        var kind = HeaderRow(connection, "format.kind");
        if (owner != Owner || kind != expected.Kind)
            throw new InvalidDataException($"{path} is a {owner}/{kind} database, not {Owner}/{expected.Kind}.");
        if (recorded > expected.Version)
            throw new NewerFormatException(path, (int)Math.Min(recorded, int.MaxValue), expected.Version);
        if (recorded != expected.Version)
            throw new InvalidDataException($"{path} records format version {recorded}; this build reads only {expected.Version}.");
    }

    private static string? HeaderRow(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM {HeaderTable} WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void SetHeaderRow(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {HeaderTable} (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }
}
