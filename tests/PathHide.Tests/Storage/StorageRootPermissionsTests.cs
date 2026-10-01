using System;
using System.IO;
using System.Runtime.Versioning;
using PathHide.Storage;
using Xunit;

namespace PathHide.Tests.Storage;

/// <summary>
/// Owner-only (0700) permissions on the storage root (storage-path conventions: "On POSIX the root is
/// owner-only (0700): created that way, and tightened to 0700 at each launch when an existing root is
/// broader"). Each test relocates the root to a throwaway directory via <c>PATHHIDE_DATA_DIR</c>, the same
/// seam <see cref="StorageRootTests"/> uses, so nothing here touches the real <c>~/.pathhide</c>.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class StorageRootPermissionsTests : IDisposable
{
    private static readonly UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string? _previousHome;
    private readonly string _target;

    public StorageRootPermissionsTests()
    {
        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        _target = Path.Combine(Path.GetTempPath(), "pathhide-perm-tests-" + NanoId.New());
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _target);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
        try
        {
            if (Directory.Exists(_target))
            {
                // The root may have been tightened to owner-only; that still permits the owner
                // (this process) to remove it.
                Directory.Delete(_target, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; a leftover throwaway temp directory does not fail the test.
        }
    }

    [Fact]
    public void EnsureExists_Does_Not_Throw_Regardless_Of_Platform()
    {
        // On Windows the owner-only step is skipped entirely (its own permission model); on POSIX it
        // runs. Either way EnsureExists must complete without throwing.
        var exception = Record.Exception(() => StorageRoot.EnsureExists());

        Assert.Null(exception);
        Assert.True(Directory.Exists(StorageRoot.Directory));
    }

    [Fact]
    public void Fresh_Root_Is_Created_Owner_Only_On_Posix()
    {
        if (!OperatingSystem.IsWindows())
        {
            AssertFreshRootIsOwnerOnly();
        }
        else
        {
            Assert.Skip("Owner-only permission bits are POSIX-only.");
        }
    }

    [Fact]
    public void Broader_Existing_Root_Is_Tightened_On_Next_EnsureExists_On_Posix()
    {
        if (!OperatingSystem.IsWindows())
        {
            AssertBroaderRootIsTightened();
        }
        else
        {
            Assert.Skip("Owner-only permission bits are POSIX-only.");
        }
    }

    [Fact]
    public void Already_Owner_Only_Root_Is_Left_Unchanged_On_Posix()
    {
        if (!OperatingSystem.IsWindows())
        {
            AssertAlreadyOwnerOnlyRootIsUnchanged();
        }
        else
        {
            Assert.Skip("Owner-only permission bits are POSIX-only.");
        }
    }

    [UnsupportedOSPlatform("windows")]
    private void AssertFreshRootIsOwnerOnly()
    {
        StorageRoot.EnsureExists();

        var mode = File.GetUnixFileMode(StorageRoot.Directory);
        Assert.Equal(OwnerOnly, mode);
    }

    [UnsupportedOSPlatform("windows")]
    private void AssertBroaderRootIsTightened()
    {
        // Create the root ahead of time with permissions broader than 0700 (group- and other-readable),
        // simulating a root left over from before this rule existed, or widened by some external actor.
        Directory.CreateDirectory(_target);
        File.SetUnixFileMode(_target,
            OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        StorageRoot.EnsureExists();

        var mode = File.GetUnixFileMode(StorageRoot.Directory);
        Assert.Equal(OwnerOnly, mode);
    }

    [UnsupportedOSPlatform("windows")]
    private void AssertAlreadyOwnerOnlyRootIsUnchanged()
    {
        StorageRoot.EnsureExists();
        var firstMode = File.GetUnixFileMode(StorageRoot.Directory);

        // A second launch against an already owner-only root must be a no-op with respect to mode.
        StorageRoot.EnsureExists();
        var secondMode = File.GetUnixFileMode(StorageRoot.Directory);

        Assert.Equal(OwnerOnly, firstMode);
        Assert.Equal(firstMode, secondMode);
    }
}
