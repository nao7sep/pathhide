using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace PathHide.Tests;

/// <summary>
/// The app version's single source of truth is <c>Directory.Build.props</c>' <c>&lt;Version&gt;</c> (per
/// the app-release conventions). Three more manifests each carry their own literal copy because the tool
/// that reads them cannot be pointed back at the SSOT, so those copies must be kept in lock-step by hand:
/// <c>macOS/Info.plist</c>'s <c>CFBundleVersion</c> and <c>CFBundleShortVersionString</c>, and
/// <c>src/PathHide/app.manifest</c>'s four-part <c>assemblyIdentity/@version</c> (the trailing part is
/// always <c>.0</c>). This reads every live file (located via <see cref="CallerFilePathAttribute"/>,
/// mirroring <c>WindowMetricsTests</c>' pattern) and fails, naming the drifted file, the moment a version
/// bump misses one of them.
/// </summary>
public sealed class VersionConsistencyTests
{
    private static readonly Regex SemVer = new(
        @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.Compiled);

    private static string RepoRoot([CallerFilePath] string callerPath = "")
    {
        // This file: <repo>/tests/PathHide.Tests/VersionConsistencyTests.cs
        var testsProjectDir = Path.GetDirectoryName(callerPath)!;
        return Path.GetFullPath(Path.Combine(testsProjectDir, "..", ".."));
    }

    private static string CanonicalVersion(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "Directory.Build.props");
        var version = XDocument.Load(path).Descendants("Version").FirstOrDefault()?.Value;

        Assert.True(!string.IsNullOrWhiteSpace(version), $"{path} is missing a <Version> element.");
        return version!;
    }

    [Fact]
    public void Directory_Build_Props_Version_Is_Well_Formed_Semver()
    {
        var canonical = CanonicalVersion(RepoRoot());

        Assert.True(
            SemVer.IsMatch(canonical),
            $"Directory.Build.props' <Version> ('{canonical}') is not a well-formed major.minor.patch version.");
    }

    [Fact]
    public void MacOS_Info_Plist_BundleVersion_Matches_The_Canonical_Version()
    {
        var repoRoot = RepoRoot();
        var canonical = CanonicalVersion(repoRoot);
        var path = Path.Combine(repoRoot, "macOS", "Info.plist");
        var actual = PlistStringValue(path, "CFBundleVersion");

        Assert.True(
            actual == canonical,
            $"macOS/Info.plist's CFBundleVersion is '{actual}' but Directory.Build.props' <Version> is " +
            $"'{canonical}'.");
    }

    [Fact]
    public void MacOS_Info_Plist_BundleShortVersionString_Matches_The_Canonical_Version()
    {
        var repoRoot = RepoRoot();
        var canonical = CanonicalVersion(repoRoot);
        var path = Path.Combine(repoRoot, "macOS", "Info.plist");
        var actual = PlistStringValue(path, "CFBundleShortVersionString");

        Assert.True(
            actual == canonical,
            $"macOS/Info.plist's CFBundleShortVersionString is '{actual}' but Directory.Build.props' " +
            $"<Version> is '{canonical}'.");
    }

    [Fact]
    public void App_Manifest_AssemblyIdentity_Version_Matches_The_Canonical_Version_With_A_Trailing_Zero()
    {
        var repoRoot = RepoRoot();
        var canonical = CanonicalVersion(repoRoot);
        var path = Path.Combine(repoRoot, "src", "PathHide", "app.manifest");
        var expected = canonical + ".0";

        XNamespace ns = "urn:schemas-microsoft-com:asm.v1";
        var actual = XDocument.Load(path).Root?.Element(ns + "assemblyIdentity")?.Attribute("version")?.Value;

        Assert.True(
            actual == expected,
            $"src/PathHide/app.manifest's assemblyIdentity version is '{actual}' but expected '{expected}' " +
            $"(Directory.Build.props' <Version> '{canonical}' plus a trailing .0).");
    }

    private static string PlistStringValue(string plistPath, string key)
    {
        var dict = XDocument.Load(plistPath).Root?.Element("dict");
        Assert.True(dict is not null, $"{plistPath} has no top-level <dict>.");

        var keyElement = dict!.Elements("key").FirstOrDefault(e => e.Value == key);
        Assert.True(keyElement is not null, $"{plistPath} is missing the key '{key}'.");

        var valueElement = keyElement!.ElementsAfterSelf().FirstOrDefault();
        Assert.True(
            valueElement is not null && valueElement.Name.LocalName == "string",
            $"{plistPath}'s '{key}' entry is not followed by a <string> value.");

        return valueElement!.Value;
    }
}
