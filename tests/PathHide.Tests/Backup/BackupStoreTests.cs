using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using PathHide.Backup;
using PathHide.Storage;
using PathHide.Tests.Storage;
using Xunit;

namespace PathHide.Tests.Backup;

/// <summary>
/// The write-through data-backup store (data-backup conventions). These exercise the real SQLite file
/// against a throwaway root redirected via <c>PATHHIDE_HOME</c> — the one relocation seam — because BLOB
/// fidelity and the dedup/insert behaviour are exactly what a fake would not exercise. The store is a
/// process-wide singleton, so each test opens against its own fresh root and closes it in teardown (which
/// releases the <c>backups.sqlite3</c> handle so the throwaway root can be deleted).
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class BackupStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousHome;

    public BackupStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pathhide-backupstore-tests", NanoId.New());
        Directory.CreateDirectory(_root);

        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _root);
        BackupStore.Close(); // fresh open against this test's root
    }

    public void Dispose()
    {
        BackupStore.Close();
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string StoreFile => Path.Combine(_root, "backups.sqlite3");

    /// <summary>A managed file path under the throwaway root. The store records the FULL absolute path,
    /// internal or external, so any absolute path is a valid subject.</summary>
    private string PathOf(string fileName) => Path.Combine(_root, fileName);

    private sealed record Row(string Path, byte[] Content, string Sha256, long ByteSize, string WrittenAtUtc);

    /// <summary>Reads all rows for a path in insert order, opening a private read connection so it never
    /// contends with the store's own singleton connection.</summary>
    private List<Row> RowsFor(string path)
    {
        var rows = new List<Row>();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = StoreFile,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT path, content, content_sha256, byte_size, written_at_utc FROM backups " +
            "WHERE path = $path ORDER BY id ASC";
        command.Parameters.AddWithValue("$path", path);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var length = reader.GetBytes(1, 0, null, 0, 0); // total BLOB length
            var content = new byte[length];
            reader.GetBytes(1, 0, content, 0, content.Length);
            rows.Add(new Row(
                reader.GetString(0),
                content,
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4)));
        }

        return rows;
    }

    [Fact]
    public void Record_StoresContentAsByteIdenticalBlob_IncludingCrLfAndNonUtf8()
    {
        // A CR/LF pair, a UTF-8 BOM, and a lone 0xFF byte (invalid UTF-8): if the store decoded to text it
        // would normalize the CR/LF, alter/drop the BOM, or corrupt 0xFF. A BLOB must round-trip verbatim.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', 0x0D, 0x0A, (byte)'b', 0xFF, 0x00, (byte)'c' };
        var path = PathOf("config.json");

        BackupStore.Record(path, bytes);

        var row = Assert.Single(RowsFor(path));
        Assert.Equal(bytes, row.Content);           // byte-identical
        Assert.Equal(bytes.Length, row.ByteSize);
        Assert.Equal(path, row.Path);               // full absolute path
    }

    [Fact]
    public void Record_Sha256_IsOverTheRawBytes_LowercaseHex()
    {
        var bytes = new byte[] { 0x00, 0xFF, 0x0D, 0x0A, 0x42 };
        var path = PathOf("paths.json");

        BackupStore.Record(path, bytes);

        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var row = Assert.Single(RowsFor(path));
        Assert.Equal(expected, row.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", row.Sha256);
    }

    [Fact]
    public void Record_WrittenAtUtc_IsSerializedIsoMs_NotTheFilenameStamp()
    {
        BackupStore.Record(PathOf("config.json"), Encoding.UTF8.GetBytes("x"));

        var row = Assert.Single(RowsFor(PathOf("config.json")));

        // The serialized ISO-8601-ms form: 2026-07-06T04:05:12.345Z — exactly three fractional digits, Z.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", row.WrittenAtUtc);

        // Explicitly NOT the yyyymmdd-hhmmss(-fff)-utc filename stamp form.
        Assert.DoesNotMatch(@"^\d{8}-\d{6}", row.WrittenAtUtc);
        Assert.DoesNotContain("-utc", row.WrittenAtUtc);

        // Round-trips as a real UTC instant (parse liberal): near now.
        var parsed = DateTimeOffset.Parse(row.WrittenAtUtc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - parsed).TotalMinutes) < 5);
    }

    [Fact]
    public void Record_UnchangedResave_IsDeduped_NoSecondRow()
    {
        var path = PathOf("config.json");
        var bytes = Encoding.UTF8.GetBytes("{\"a\":1}");

        BackupStore.Record(path, bytes);
        BackupStore.Record(path, (byte[])bytes.Clone()); // identical content, distinct buffer

        Assert.Single(RowsFor(path)); // dedup skipped the second insert
    }

    [Fact]
    public void Record_ChangedSave_InsertsANewRow()
    {
        var path = PathOf("config.json");
        BackupStore.Record(path, Encoding.UTF8.GetBytes("{\"a\":1}"));
        BackupStore.Record(path, Encoding.UTF8.GetBytes("{\"a\":2}"));

        var rows = RowsFor(path);
        Assert.Equal(2, rows.Count);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(rows[0].Content));
        Assert.Equal("{\"a\":2}", Encoding.UTF8.GetString(rows[1].Content));
    }

    [Fact]
    public void Record_Revert_InsertsANewRow_ComparingOnlyAgainstTheLatest()
    {
        // A revert differs from the IMMEDIATELY preceding row, so it is recorded as the new version it is —
        // dedup compares only against the latest row for the path, not the whole history.
        var path = PathOf("config.json");
        var v1 = Encoding.UTF8.GetBytes("{\"a\":1}");
        var v2 = Encoding.UTF8.GetBytes("{\"a\":2}");

        BackupStore.Record(path, v1);
        BackupStore.Record(path, v2);
        BackupStore.Record(path, (byte[])v1.Clone()); // revert to v1's content

        var rows = RowsFor(path);
        Assert.Equal(3, rows.Count);
        Assert.Equal(v1, rows[0].Content);
        Assert.Equal(v2, rows[1].Content);
        Assert.Equal(v1, rows[2].Content); // the revert is a distinct third row
    }

    [Fact]
    public void Record_DedupsPerPath_NotAcrossPaths()
    {
        var a = PathOf("config.json");
        var b = PathOf("paths.json");
        var same = Encoding.UTF8.GetBytes("same-content");

        BackupStore.Record(a, same);
        BackupStore.Record(b, (byte[])same.Clone()); // identical bytes, different path

        Assert.Single(RowsFor(a));
        Assert.Single(RowsFor(b)); // per-path dedup: b is not skipped just because a has the same content
    }

    [Fact]
    public void Store_RunsInWalMode()
    {
        BackupStore.Record(PathOf("config.json"), Encoding.UTF8.GetBytes("x"));
        // WAL leaves the -wal and -shm sidecars beside the store — normal SQLite artifacts, not stray files.
        BackupStore.Close(); // flush; checkpoint may leave/remove sidecars, so assert the mode via PRAGMA.

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = StoreFile,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", ((string)command.ExecuteScalar()!).ToLowerInvariant());
    }

    [Fact]
    public void Record_BestEffort_OpenFailureDisablesRecordingWithoutThrowing()
    {
        // Point PATHHIDE_HOME at a location the store cannot open a DB in: a *file* standing where the root
        // directory would be. EnsureOpen's mkdir/-open fails, so recording is disabled for the session —
        // one warn is logged and every Record is a silent no-op that never throws.
        var blocker = Path.Combine(Path.GetTempPath(), "pathhide-blocked-" + NanoId.New());
        File.WriteAllText(blocker, "not a directory"); // a file where the store expects a directory
        var previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        try
        {
            BackupStore.Close();
            Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, blocker);

            // The record must not throw even though the store cannot be opened (best-effort contract).
            var exception = Record.Exception(() =>
                BackupStore.Record(Path.Combine(blocker, "config.json"), Encoding.UTF8.GetBytes("x")));
            Assert.Null(exception);
        }
        finally
        {
            BackupStore.Close();
            Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, previousHome);
            try { File.Delete(blocker); } catch { /* best-effort */ }
        }
    }
}
