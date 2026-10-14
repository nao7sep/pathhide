using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

/// <summary>
/// The extended-length spelling the data folder's identity is resolved from, which runs anywhere; the
/// resolution itself is native and covered by the Windows platform tests.
/// </summary>
public sealed class WindowsDirectoryIdentityTests
{
    [Theory]
    [InlineData(@"C:\Users\me\.pathhide", @"\\?\C:\Users\me\.pathhide")]
    [InlineData(@"\\server\share\data", @"\\?\UNC\server\share\data")]
    // Already extended, whether a drive or a share: unchanged.
    [InlineData(@"\\?\C:\Users\me\.pathhide", @"\\?\C:\Users\me\.pathhide")]
    [InlineData(@"\\?\UNC\server\share\data", @"\\?\UNC\server\share\data")]
    // A device path names a local object, not a share called ".".
    [InlineData(@"\\.\C:\Users\me\.pathhide", @"\\?\C:\Users\me\.pathhide")]
    public void A_full_path_takes_its_extended_length_spelling(string fullPath, string expected) =>
        Assert.Equal(expected, WindowsDirectoryIdentity.ToExtendedPath(fullPath));
}
