using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using CommunityToolkit.Mvvm.Input;
using PathHide.Backup;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using PathHide.Tests.Fakes;
using PathHide.Tests.Storage;
using PathHide.ViewModels;
using Xunit;

namespace PathHide.Tests.Backup;

/// <summary>
/// The data-backup history (data-backup conventions). These exercise the real SQLite file against a
/// throwaway root redirected via <c>PATHHIDE_DATA_DIR</c> — the one relocation seam — because BLOB fidelity,
/// the per-session rows and the off-save writer are exactly what a fake would not exercise. The store is a
/// process-wide singleton, so each test opens against its own fresh root and closes it in teardown (which
/// waits for pending writes and releases the <c>backups.sqlite3</c> handle so the root can be deleted).
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class BackupStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousHome;
    private readonly string _session = BackupStore.Session;

    public BackupStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pathhide-backupstore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, _root);
        BackupStore.Close(); // fresh open against this test's root
    }

    public void Dispose()
    {
        BackupStore.Close();
        BackupStore.Session = _session;
        // Closing returns each connection to Microsoft.Data.Sqlite's pool, which keeps its file open;
        // Windows cannot delete an open database file.
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, _previousHome);
        Directory.Delete(_root, recursive: true);
    }

    private string StoreFile => Path.Combine(_root, "backups.sqlite3");

    /// <summary>A managed file path under the throwaway root. The store records the FULL absolute path,
    /// internal or external, so any absolute path is a valid subject.</summary>
    private string PathOf(string fileName) => Path.Combine(_root, fileName);

    private sealed record Row(string? Session, string Path, byte[] Content, string Sha256, long ByteSize, string WrittenAtUtc);

    /// <summary>Reads all rows for a path in insert order once the pending writes have landed, opening a
    /// private read connection.</summary>
    private List<Row> RowsFor(string path)
    {
        BackupStore.Close();
        return ReadRows(path);
    }

    /// <summary>Reads the rows written so far, without waiting for pending writes.</summary>
    private List<Row> ReadRows(string path)
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
            "SELECT session_id, path, content, content_sha256, byte_size, written_at_utc FROM backups " +
            "WHERE path = $path ORDER BY id ASC";
        command.Parameters.AddWithValue("$path", path);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var length = reader.GetBytes(2, 0, null, 0, 0); // total BLOB length
            var content = new byte[length];
            reader.GetBytes(2, 0, content, 0, content.Length);
            rows.Add(new Row(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetString(1),
                content,
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5)));
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
    public void Record_ChangedSaveInOneSession_ReplacesTheSessionsRow()
    {
        var path = PathOf("config.json");
        BackupStore.Record(path, Encoding.UTF8.GetBytes("{\"a\":1}"));
        BackupStore.Close();
        BackupStore.Record(path, Encoding.UTF8.GetBytes("{\"a\":2}"));

        var row = Assert.Single(RowsFor(path));
        Assert.Equal("{\"a\":2}", Encoding.UTF8.GetString(row.Content));
        Assert.Equal(BackupStore.Session, row.Session);
    }

    [Fact]
    public void Record_KeepsEachSessionsLastVersion_AndNeverChangesAnEarlierSessionsRow()
    {
        var path = PathOf("config.json");
        BackupStore.Session = "earlier";
        BackupStore.Record(path, Encoding.UTF8.GetBytes("v1"));
        BackupStore.Close();

        BackupStore.Session = "later";
        BackupStore.Record(path, Encoding.UTF8.GetBytes("v2"));
        BackupStore.Close();
        BackupStore.Record(path, Encoding.UTF8.GetBytes("v3"));

        var rows = RowsFor(path);
        Assert.Equal([("earlier", "v1"), ("later", "v3")],
            rows.Select(row => (row.Session, Encoding.UTF8.GetString(row.Content))));
    }

    [Fact]
    public void Record_FirstSaveEqualToTheLatestEarlierRow_WritesNothing()
    {
        var path = PathOf("config.json");
        BackupStore.Session = "earlier";
        BackupStore.Record(path, Encoding.UTF8.GetBytes("same"));
        BackupStore.Close();

        BackupStore.Session = "later";
        BackupStore.Record(path, Encoding.UTF8.GetBytes("same"));

        Assert.Equal("earlier", Assert.Single(RowsFor(path)).Session);
    }

    [Fact]
    public void Record_KeepsRowsFromBeforeSessions_AndAddsTheColumnInPlace()
    {
        // A store written by a build without sessions: same table and version, no session_id column.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = StoreFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE backups (id INTEGER PRIMARY KEY, path TEXT NOT NULL, content BLOB NOT NULL,
                  content_sha256 TEXT NOT NULL, byte_size INTEGER NOT NULL, written_at_utc TEXT NOT NULL);
                CREATE INDEX idx_backups_path_id ON backups (path, id);
                INSERT INTO backups (path, content, content_sha256, byte_size, written_at_utc)
                  VALUES ($path, X'6F6C64', 'x', 3, '2026-01-01T00:00:00.000Z');
                PRAGMA user_version = 1;
                """;
            command.Parameters.AddWithValue("$path", PathOf("config.json"));
            command.ExecuteNonQuery();
        }

        BackupStore.Record(PathOf("config.json"), Encoding.UTF8.GetBytes("new"));

        var rows = RowsFor(PathOf("config.json"));
        Assert.Equal([(null, "old"), (BackupStore.Session, "new")],
            rows.Select(row => (row.Session, Encoding.UTF8.GetString(row.Content))));
    }

    /// <summary>Holds SQLite's write lock on the store from another connection, as a stalled store would.</summary>
    private SqliteConnection HoldWriteLock()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = StoreFile,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void Release(SqliteConnection holder)
    {
        using (var command = holder.CreateCommand())
        {
            command.CommandText = "ROLLBACK;";
            command.ExecuteNonQuery();
        }
        holder.Dispose();
    }

    [Fact]
    public void Save_CompletesWhileTheHistoryIsStalled_AndTheHistoryCatchesUpAfterwards()
    {
        // The save's return while another connection still holds the store's write lock shows it did not
        // wait for the history; the row landing after release shows the write was kept, not dropped.
        var store = new PathListStore();
        store.Save([new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Shown }]);
        BackupStore.Close();
        var holder = HoldWriteLock();

        store.Save([new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Hidden }]);
        Release(holder);

        Assert.Equal(File.ReadAllBytes(PathOf("paths.json")), Assert.Single(RowsFor(PathOf("paths.json"))).Content);
    }

    [Fact(Timeout = 30_000)]
    public async Task Hide_AppliesAttributesWhileTheHistoryIsStalled()
    {
        var visibility = new FakeVisibilityService();
        var settings = new FakeSettingsStore();
        var vm = new MainWindowViewModel(new BoundedVisibility(visibility), new PathListStore(), settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState());
        vm.Initialize();
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/a" });
        await vm.ScanTask;
        BackupStore.Close();
        var holder = HoldWriteLock();
        try
        {
            vm.Rows.Single().IsSelected = true;
            await ((IAsyncRelayCommand)vm.HideSelectedCommand).ExecuteAsync(null);

            Assert.Contains("/a", visibility.Hidden);
            Assert.Contains("\"hidden\"", File.ReadAllText(PathOf("paths.json")));
        }
        finally
        {
            Release(holder);
        }
        Assert.Contains("\"hidden\"", Encoding.UTF8.GetString(Assert.Single(RowsFor(PathOf("paths.json"))).Content));
    }

    [Fact(Timeout = 30_000)]
    public async Task Flush_WaitsForPendingHistory()
    {
        BackupStore.Record(PathOf("config.json"), Encoding.UTF8.GetBytes("first"));
        BackupStore.Close();
        var holder = HoldWriteLock();
        BackupStore.Record(PathOf("config.json"), Encoding.UTF8.GetBytes("second"));

        var flush = BackupStore.FlushAsync(TimeSpan.FromHours(1), TimeProvider.System);
        Release(holder);
        await flush;

        Assert.Equal("second", Encoding.UTF8.GetString(Assert.Single(ReadRows(PathOf("config.json"))).Content));
    }

    [Fact(Timeout = 30_000)]
    public async Task Flush_WithNoTimeLeft_ReturnsWithoutWaiting()
    {
        BackupStore.Record(PathOf("config.json"), Encoding.UTF8.GetBytes("first"));
        BackupStore.Close();
        var holder = HoldWriteLock();
        try
        {
            BackupStore.Record(PathOf("config.json"), Encoding.UTF8.GetBytes("second"));

            await BackupStore.FlushAsync(TimeSpan.Zero, TimeProvider.System);

            Assert.Equal("first", Encoding.UTF8.GetString(Assert.Single(ReadRows(PathOf("config.json"))).Content));
        }
        finally
        {
            Release(holder);
        }
    }

    private static MainWindowViewModel QuittingViewModel()
    {
        var settings = new FakeSettingsStore();
        // A bound no test outlasts: completing at all shows whether the quit waited for the history.
        return new MainWindowViewModel(new BoundedVisibility(new FakeVisibilityService()),
            new FakeJsonStore<List<PathEntry>>(), settings, settings.Load().Value, new FakeJsonStore<AppState>(),
            new AppState())
        {
            ShutdownBound = TimeSpan.FromHours(1),
        };
    }

    [Fact(Timeout = 30_000)]
    public async Task Quit_WaitsForPendingHistory()
    {
        BackupStore.Record(PathOf("config.json"), Encoding.UTF8.GetBytes("first"));
        BackupStore.Close();
        var holder = HoldWriteLock();
        BackupStore.Record(PathOf("config.json"), Encoding.UTF8.GetBytes("second"));

        var quit = QuittingViewModel().QuitAsync();
        Release(holder);

        Assert.True(await quit);
        Assert.Equal("second", Encoding.UTF8.GetString(Assert.Single(ReadRows(PathOf("config.json"))).Content));
    }

    [Fact(Timeout = 30_000)]
    public async Task SessionEnd_DoesNotWaitForPendingHistory()
    {
        BackupStore.Record(PathOf("config.json"), Encoding.UTF8.GetBytes("first"));
        BackupStore.Close();
        var holder = HoldWriteLock();
        try
        {
            BackupStore.Record(PathOf("config.json"), Encoding.UTF8.GetBytes("second"));

            await QuittingViewModel().EndSessionAsync();

            Assert.Equal("first", Encoding.UTF8.GetString(Assert.Single(ReadRows(PathOf("config.json"))).Content));
        }
        finally
        {
            Release(holder);
        }
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
    public void Record_BestEffort_DoesNotThrowWhenTheStorageRootItselfCannotBeResolved()
    {
        // The harder half of the same contract. An open failure was already handled; a root that
        // cannot be RESOLVED throws from StorageRoot.Directory, and EnsureOpen used to call it
        // again from inside its own catch to name the file it failed on - so that second throw
        // escaped EnsureOpen, escaped Record, and surfaced in JsonStore's atomic write. The save
        // was already on disk by then, so the user saw "Failed to save" for a save that succeeded
        // and the in-memory list rolled back, leaving memory disagreeing with disk.
        var previousHome = Environment.GetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable);
        try
        {
            BackupStore.Close();
            // Expands to nothing, which the resolver rejects rather than falling back.
            Environment.SetEnvironmentVariable(
                StorageRoot.DataDirEnvironmentVariable, "$PATHHIDE_DEFINITELY_UNSET_FOR_TEST");

            Assert.Throws<InvalidOperationException>(() => _ = StorageRoot.Directory);

            var exception = Record.Exception(() =>
                BackupStore.Record("/somewhere/config.json", Encoding.UTF8.GetBytes("x")));
            Assert.Null(exception);
        }
        finally
        {
            BackupStore.Close();
            Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, previousHome);
        }
    }

    [Fact]
    public void Record_BestEffort_OpenFailureDisablesRecordingWithoutThrowing()
    {
        // Point PATHHIDE_DATA_DIR at a location the store cannot open a DB in: a *file* standing where the root
        // directory would be. EnsureOpen's mkdir/-open fails, so recording is disabled for the session —
        // one warn is logged and every Record is a silent no-op that never throws.
        var blocker = Path.Combine(Path.GetTempPath(), "pathhide-blocked-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory"); // a file where the store expects a directory
        var previousHome = Environment.GetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable);
        try
        {
            BackupStore.Close();
            Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, blocker);

            // The record must not throw even though the store cannot be opened (best-effort contract).
            var exception = Record.Exception(() =>
                BackupStore.Record(Path.Combine(blocker, "config.json"), Encoding.UTF8.GetBytes("x")));
            Assert.Null(exception);
        }
        finally
        {
            BackupStore.Close();
            Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, previousHome);
            try { File.Delete(blocker); } catch { /* best-effort */ }
        }
    }
}
