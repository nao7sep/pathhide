using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace PathHide.Storage;

/// <summary>What the level filter offers: a record's own level, or every record at warn or error.</summary>
public enum RecordLevelFilter
{
    WarningsAndErrors,
    Error,
    Warn,
    Info,
    Debug,
}

/// <summary>Where the next page starts: the last record of the page before it.</summary>
public readonly record struct RecordCursor(string Time, long Id);

/// <summary>
/// A page of records to read: one launch (named by its session) or all of them, a level or all of
/// them, the words to search for, and where the page starts.
/// </summary>
public sealed record RecordsQuery(string? Session, RecordLevelFilter? Level, string Search, RecordCursor? After);

/// <summary>One record as the list shows it.</summary>
public sealed record RecordSummary(long Id, string Session, string Time, string Level, string Message);

/// <summary>A page of records, newest first, and whether more follow it.</summary>
public sealed record RecordsPage(IReadOnlyList<RecordSummary> Records, bool More);

/// <summary>One record whole: every column as stored. The fields and the error are the JSON text the database holds.</summary>
public sealed record RecordDetail(
    long Id, string Session, string Time, string Level, string Message, string? Fields, string? Error);

/// <summary>What the records window asks of the records database.</summary>
public interface IRecordsReader
{
    Task<RecordsPage> ReadPageAsync(RecordsQuery query);

    /// <summary>The record, or null when there is none with that id.</summary>
    Task<RecordDetail?> ReadDetailAsync(long id);

    /// <summary>Every session that has records, newest first.</summary>
    Task<IReadOnlyList<string>> ReadSessionsAsync();
}

/// <summary>
/// Reads <c>records.sqlite3</c> for the records window. Each read opens its own read-only connection on
/// a worker thread, beside the logger's writing one, which the database's WAL journal lets run
/// alongside it; the logger alone writes. A read waits at most <see cref="ReadBound"/> (PLAYBOOK, Bound
/// every external wait), and then fails while the read it gave up on finishes or fails on its own.
/// </summary>
/// <remarks>
/// A read logs nothing: every record the database stores signals the records window to read again, so a
/// read that logged would keep an open window reading forever. The caller reports a failed read.
/// </remarks>
public sealed class RecordsReader(string databasePath) : IRecordsReader
{
    public const int PageSize = 100;

    internal static readonly TimeSpan ReadBound = TimeSpan.FromSeconds(10);

    private const string Searched = "message LIKE $search ESCAPE '\\' OR fields LIKE $search ESCAPE '\\' OR error LIKE $search ESCAPE '\\'";

    public Task<RecordsPage> ReadPageAsync(RecordsQuery query) => Read(connection => ReadPage(connection, query));

    public Task<RecordDetail?> ReadDetailAsync(long id) => Read(connection => ReadDetail(connection, id));

    public Task<IReadOnlyList<string>> ReadSessionsAsync() => Read(ReadSessions);

    private Task<T> Read<T>(Func<SqliteConnection, T> read) =>
        Task.Run(() =>
        {
            // Pooling off, so a read leaves no handle on the file behind it.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout = 5000;";
                pragma.ExecuteNonQuery();
            }
            FormatVersions.CheckDatabase(connection, databasePath, FormatVersions.Records);
            return read(connection);
        }).WaitAsync(ReadBound);

    private static RecordsPage ReadPage(SqliteConnection connection, RecordsQuery query)
    {
        using var command = connection.CreateCommand();
        var where = new List<string>();
        if (query.Session is { } session)
        {
            where.Add("session = $session");
            command.Parameters.AddWithValue("$session", session);
        }
        if (query.Level is { } level)
        {
            if (level == RecordLevelFilter.WarningsAndErrors)
            {
                where.Add("level IN ('warn', 'error')");
            }
            else
            {
                where.Add("level = $level");
                command.Parameters.AddWithValue("$level", LevelName(level));
            }
        }
        if (LikePattern(query.Search) is { } pattern)
        {
            where.Add($"({Searched})");
            command.Parameters.AddWithValue("$search", pattern);
        }
        if (query.After is { } after)
        {
            where.Add("(time < $afterTime OR (time = $afterTime AND id < $afterId))");
            command.Parameters.AddWithValue("$afterTime", after.Time);
            command.Parameters.AddWithValue("$afterId", after.Id);
        }

        command.CommandText =
            "SELECT id, session, time, level, message FROM log_entries" +
            (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
            " ORDER BY time DESC, id DESC LIMIT $limit";
        // One past the page, which says whether another follows.
        command.Parameters.AddWithValue("$limit", PageSize + 1);

        var records = new List<RecordSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new RecordSummary(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        var more = records.Count > PageSize;
        if (more)
            records.RemoveAt(PageSize);
        return new RecordsPage(records, more);
    }

    private static RecordDetail? ReadDetail(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, session, time, level, message, fields, error FROM log_entries WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new RecordDetail(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static IReadOnlyList<string> ReadSessions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // A session is its launch's start in the serialized UTC form, so its text sorts by time.
        command.CommandText = "SELECT DISTINCT session FROM log_entries ORDER BY session DESC";
        var sessions = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            sessions.Add(reader.GetString(0));
        return sessions;
    }

    // The search as a LIKE pattern that matches it anywhere, its own wildcards taken literally; null
    // when there is nothing to search for.
    internal static string? LikePattern(string search)
    {
        var trimmed = search.Trim();
        if (trimmed.Length == 0)
            return null;

        return "%" + trimmed.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
    }

    internal static string LevelName(RecordLevelFilter level) => level switch
    {
        RecordLevelFilter.Error => "error",
        RecordLevelFilter.Warn => "warn",
        RecordLevelFilter.Info => "info",
        RecordLevelFilter.Debug => "debug",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Warnings and errors is not one level."),
    };
}
