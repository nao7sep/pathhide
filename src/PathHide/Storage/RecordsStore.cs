using System;
using System.IO;
using Microsoft.Data.Sqlite;
using PathHide.Services;

namespace PathHide.Storage;

/// <summary>
/// The records database, <c>records.sqlite3</c> under the storage root (data-lifecycle conventions,
/// <em>Records</em>). PathHide's only records are log entries, each a row with its session; the app
/// process alone opens it, and the elevated child's entries reach it through the app. The records
/// window reads it through <see cref="RecordsReader"/>.
/// </summary>
public sealed class RecordsStore : ILogSink
{
    public const string FileName = "records.sqlite3";

    // No retention: logging conventions, Never deleted. The session index serves reading one launch's
    // entries in order and listing the launches; the time index serves the records window's pages,
    // newest first (RecordsReader).
    private const string Schema = @"
CREATE TABLE IF NOT EXISTS log_entries (
  id      INTEGER PRIMARY KEY,
  session TEXT NOT NULL,
  time    TEXT NOT NULL,
  level   TEXT NOT NULL,
  message TEXT NOT NULL,
  fields  TEXT,
  error   TEXT
);
CREATE INDEX IF NOT EXISTS idx_log_entries_session_id ON log_entries (session, id);
CREATE INDEX IF NOT EXISTS idx_log_entries_time_id ON log_entries (time, id);
";

    // Used only under the owning SessionLogger's lock.
    private readonly SqliteConnection _connection;

    private RecordsStore(SqliteConnection connection) => _connection = connection;

    /// <summary>Opens or creates the database at <paramref name="path"/>; null with the reason when it cannot.</summary>
    public static RecordsStore? TryOpen(string path, out Exception? failure)
    {
        SqliteConnection? connection = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            connection = FormatVersions.OpenDatabase(path, FormatVersions.Records, Schema);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
                command.ExecuteNonQuery();
            }

            failure = null;
            return new RecordsStore(connection);
        }
        catch (Exception ex)
        {
            try { connection?.Dispose(); }
            catch (Exception cleanup) { Log.Warn("records: failed to close after open failure", cleanup, new { path }); }
            failure = ex;
            return null;
        }
    }

    public void Write(LogEntry entry)
    {
        using var insert = _connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO log_entries (session, time, level, message, fields, error) " +
            "VALUES ($session, $time, $level, $message, $fields, $error)";
        insert.Parameters.AddWithValue("$session", entry.Session);
        insert.Parameters.AddWithValue("$time", entry.Time);
        insert.Parameters.AddWithValue("$level", entry.Level);
        insert.Parameters.AddWithValue("$message", entry.Message);
        insert.Parameters.AddWithValue("$fields", (object?)LogEntry.ToText(entry.Fields) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$error", (object?)LogEntry.ToText(entry.Error) ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
