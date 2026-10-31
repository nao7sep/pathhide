using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using PathHide.Backup;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using Xunit;

namespace PathHide.Tests.Storage;

/// <summary>Real NTFS read denial: a replaceable preference differs from a protected valuable list.</summary>
[SupportedOSPlatform("windows")]
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class WindowsStoreRecoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pathhide-win-recovery-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable);
    private readonly Dictionary<string, FileSecurity> _permissions = [];

    public WindowsStoreRecoveryTests()
    {
        BackupStore.Close();
        Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, _root);
    }

    private string DenyReading(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        var file = new FileInfo(path);
        _permissions.Add(path, file.GetAccessControl());
        var denied = file.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ReadData, AccessControlType.Deny));
        file.SetAccessControl(denied);
        Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllBytes(path));
        return path;
    }

    private void Restore(string path) => new FileInfo(path).SetAccessControl(_permissions[path]);

    public void Dispose()
    {
        try
        {
            Log.Shutdown();
            BackupStore.Close();
            SqliteConnection.ClearAllPools();
            foreach (var path in _permissions.Keys)
                Restore(path);
            Directory.Delete(_root, recursive: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, _previous);
        }
    }

    [WindowsOnlyFact]
    public void UnreadableConfig_UsesDefaultsAndAChangedSaveReplacesIt()
    {
        var path = DenyReading(AppSettings.FileName, "{\"format\":{\"owner\":\"pathhide\",\"kind\":\"config\",\"version\":1},\"writer\":{\"name\":\"pathhide\",\"version\":\"1.0.0\"},\"theme\":\"dark\"}");
        var store = new SettingsStore();
        var loaded = store.Load();
        Assert.True(loaded.WasUnreadable);
        Assert.Equal(ThemePreference.System, loaded.Value.Theme);

        Assert.True(store.SaveChanges(loaded.Value, new AppSettings { Theme = ThemePreference.Light }));

        Restore(path);
        Assert.Equal(ThemePreference.Light, store.Load().Value.Theme);
    }

    [WindowsOnlyFact]
    public void UnreadableState_CanBeReplacedInAWritableDirectory()
    {
        var path = DenyReading(AppState.FileName, "{\"format\":{\"owner\":\"pathhide\",\"kind\":\"state\",\"version\":1},\"writer\":{\"name\":\"pathhide\",\"version\":\"1.0.0\"},\"windowWidth\":700}");
        var store = new JsonStore<AppState>(AppState.FileName, "state", FileFormats.State, recordBackup: false);
        Assert.True(store.Load().WasUnreadable);

        store.Save(new AppState { WindowWidth = 900 });

        Restore(path);
        Assert.Equal(900, store.Load().Value.WindowWidth);
    }

    [WindowsOnlyFact]
    public void UnreadablePaths_RefuseLoadAndSaveAndKeepTheOriginalBytes()
    {
        const string original = "{\"format\":{\"owner\":\"pathhide\",\"kind\":\"pathList\",\"version\":1},\"writer\":{\"name\":\"pathhide\",\"version\":\"1.0.0\"},\"paths\":[]}";
        var path = DenyReading(PathListStore.FileName, original);
        var store = new PathListStore();
        Assert.True(Assert.Throws<UnreadableStoreException>(() => store.Load()).IsAccessFailure);
        Assert.Throws<UnreadableStoreException>(() => store.Save([]));

        Restore(path);
        Assert.Equal(original, File.ReadAllText(path));
    }
}
