using PathHide.Models;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

public class PathNormalizerTests
{
    // --- TryNormalize: POSIX ---

    [Theory]
    [InlineData("/foo/bar", "/foo/bar")]
    [InlineData("/foo/bar/", "/foo/bar")]   // trailing separator stripped
    [InlineData("/foo/bar///", "/foo/bar")] // TrimEnd removes the whole trailing run
    [InlineData("/", "/")]                  // root preserved
    [InlineData("/a", "/a")]
    public void TryNormalize_Posix_NormalizesAndKeepsFamily(string input, string expected)
    {
        var ok = PathNormalizer.TryNormalize(input, out var normalized, out var family);

        Assert.True(ok);
        Assert.Equal(expected, normalized);
        Assert.Equal(PathFamily.Posix, family);
    }

    // --- TryNormalize: Windows drive-rooted ---

    [Theory]
    [InlineData(@"C:\foo\bar", @"C:\foo\bar")]
    [InlineData(@"C:\foo\bar\", @"C:\foo\bar")]   // trailing separator stripped
    [InlineData("C:/foo/bar", @"C:\foo\bar")]     // forward slashes converted
    [InlineData("C:/foo/bar/", @"C:\foo\bar")]
    [InlineData(@"C:\", @"C:\")]                   // drive root preserved
    [InlineData(@"z:\Temp", @"z:\Temp")]           // lowercase drive letter accepted
    public void TryNormalize_Windows_NormalizesAndKeepsFamily(string input, string expected)
    {
        var ok = PathNormalizer.TryNormalize(input, out var normalized, out var family);

        Assert.True(ok);
        Assert.Equal(expected, normalized);
        Assert.Equal(PathFamily.Windows, family);
    }

    // --- TryNormalize: UNC ---

    [Theory]
    [InlineData(@"\\server\share", @"\\server\share")]           // complete root preserved
    [InlineData(@"\\server\share\", @"\\server\share")]          // trailing stripped, root kept
    [InlineData(@"\\server\share\folder\", @"\\server\share\folder")]
    [InlineData("//server/share/folder", @"\\server\share\folder")] // forward slashes converted
    [InlineData(@"\\server\", @"\\server\")]                     // incomplete UNC returned as-is
    public void TryNormalize_Unc_NormalizesAndKeepsFamily(string input, string expected)
    {
        var ok = PathNormalizer.TryNormalize(input, out var normalized, out var family);

        Assert.True(ok);
        Assert.Equal(expected, normalized);
        Assert.Equal(PathFamily.Unc, family);
    }

    // --- TryNormalize: rejected inputs ---

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("foo/bar")]      // relative
    [InlineData("foo")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("C:")]           // too short to be drive-rooted
    [InlineData("C:foo")]        // drive-relative, no separator after colon
    [InlineData("1:\\foo")]      // not an ASCII letter drive
    public void TryNormalize_RejectsNonAbsoluteInput(string input)
    {
        var ok = PathNormalizer.TryNormalize(input, out var normalized, out _);

        Assert.False(ok);
        Assert.Null(normalized);
    }

    [Fact]
    public void TryNormalize_NullInput_ReturnsFalse()
    {
        var ok = PathNormalizer.TryNormalize(null!, out var normalized, out _);

        Assert.False(ok);
        Assert.Null(normalized);
    }

    // --- AreEqual: POSIX is case-sensitive ---

    [Fact]
    public void AreEqual_Posix_IsCaseSensitive()
    {
        Assert.True(PathNormalizer.AreEqual("/foo/bar", "/foo/bar/"));   // trailing-slash insensitive
        Assert.False(PathNormalizer.AreEqual("/Foo", "/foo"));          // case matters on POSIX
    }

    // --- AreEqual: Windows / UNC are case-insensitive ---

    [Fact]
    public void AreEqual_Windows_IsCaseInsensitive()
    {
        Assert.True(PathNormalizer.AreEqual(@"C:\Foo\Bar", @"c:\foo\bar"));
        Assert.True(PathNormalizer.AreEqual(@"C:\Foo", "C:/foo/"));     // slash + trailing differences ignored
    }

    [Fact]
    public void AreEqual_Unc_IsCaseInsensitive()
    {
        Assert.True(PathNormalizer.AreEqual(@"\\Server\Share\Folder", "//server/share/folder"));
    }

    // --- AreEqual: family mismatch ---

    [Fact]
    public void AreEqual_DifferentFamilies_AreNotEqual()
    {
        Assert.False(PathNormalizer.AreEqual("/foo", @"C:\foo"));
    }

    // --- AreEqual: fallback when one or both inputs don't normalize ---

    [Fact]
    public void AreEqual_BothUnnormalizable_FallsBackToOrdinal()
    {
        Assert.True(PathNormalizer.AreEqual("relative/path", "relative/path"));
        Assert.False(PathNormalizer.AreEqual("relative/path", "Relative/path"));
    }

    [Fact]
    public void AreEqual_OneNormalizableOneNot_AreNotEqual()
    {
        Assert.False(PathNormalizer.AreEqual("/foo", "foo"));
    }
}
