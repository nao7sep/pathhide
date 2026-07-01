using PathHide.Backup;
using Xunit;

namespace PathHide.Tests.Backup;

/// <summary>
/// The pure home-file mapping: a file's relative path is its forward-slash archive entry path.
/// </summary>
public sealed class BackupArchivePathsTests
{
    [Theory]
    [InlineData("config.json", "config.json")]
    [InlineData("paths.json", "paths.json")]
    [InlineData("data/settings.json", "data/settings.json")]
    public void ForHomeFile_KeepsRelativePath(string relative, string expected) =>
        Assert.Equal(expected, BackupArchivePaths.ForHomeFile(relative));

    [Theory]
    [InlineData("data\\settings.json", "data/settings.json")]
    [InlineData("/config.json", "config.json")]
    [InlineData("\\config.json", "config.json")]
    public void Normalize_UsesForwardSlashesAndTrimsLeadingSeparator(string input, string expected) =>
        Assert.Equal(expected, BackupArchivePaths.Normalize(input));
}
