using System;
using System.IO;
using Microsoft.Data.Sqlite;
using PathHide.Services;

namespace PathHide.Storage;

/// <summary>
/// The records database, <c>records.sqlite3</c> under the storage root (data-lifecycle conventions,
/// <em>Records</em>). PathHide's only records are log entries, each a row with its session; the app
/// process alone opens it, and the elevated child's entries reach it through the app.
/// </summary>
public sealed class RecordsStore : ILogSink
{
    public const string FileName = "records.sqlite3";

    // No retention: logging conventions, Never deleted. The session index serves reading one launch's
    // entries in order.
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

            // not recorded: a binary store written here, never through the managed-text atomic-write
            // path (data-backup conventions).
            // Pooling off, so closing the store releases the file.
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                // WAL with synchronous NORMAL keeps every committed entry through a process crash
                // without a disk sync per line.
                command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;";
                command.ExecuteNonQuery();
                command.CommandText = Schema;
                command.ExecuteNonQuery();
            }

            failure = null;
            return new RecordsStore(connection);
        }
        catch (Exception ex)
        {
            connection?.Dispose();
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
