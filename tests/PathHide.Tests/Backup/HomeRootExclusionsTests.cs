using PathHide.Backup;
using Xunit;

namespace PathHide.Tests.Backup;

/// <summary>
/// The pure exclude decision: durable data is kept, feature-owned/throwaway/OS-metadata paths are dropped.
/// This is the "did we pick the right files?" logic, so it is tested directly, with no I/O.
/// </summary>
public sealed class HomeRootExclusionsTests
{
    [Theory]
    [InlineData("config.json")]
    [InlineData("paths.json")]
    [InlineData("data/whatever.json")]
    public void DurableFiles_AreKept(string path) =>
        Assert.False(HomeRootExclusions.IsExcluded(path));

    [Theory]
    [InlineData("logs/")]
    [InlineData("logs/20260701-000000-utc.log")]
    [InlineData("backups/")]
    [InlineData("backups/index.json")]
    [InlineData("backups/backup-20260701-000000-utc.zip")]
    [InlineData("config.json.tmp")]
    [InlineData("config.json.bak")]
    [InlineData("paths.json.bak")]
    [InlineData(".DS_Store")]
    [InlineData("Thumbs.db")]
    [InlineData("desktop.ini")]
    [InlineData("sub/.DS_Store")]
    public void ThrowawayAndFeatureOwnedPaths_AreExcluded(string path) =>
        Assert.True(HomeRootExclusions.IsExcluded(path));

    [Theory]
    [InlineData("BACKUPS/index.json")]
    [InlineData("Logs/session.log")]
    [InlineData("CONFIG.JSON.BAK")]
    [InlineData("thumbs.DB")]
    [InlineData("Desktop.ini")]
    public void Exclusions_AreCaseInsensitive(string path) =>
        Assert.True(HomeRootExclusions.IsExcluded(path));
}
