using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace PathHide.Tests;

public sealed class InstallerConfigurationTests
{
    private static string InstallerScript([CallerFilePath] string callerPath = "")
    {
        var testsProjectDir = Path.GetDirectoryName(callerPath)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testsProjectDir, "..", ".."));
        return File.ReadAllText(Path.Combine(repoRoot, "scripts", "pathhide.iss"));
    }

    [Fact]
    public void Installer_Supports_Both_Install_Modes_Without_An_Admin_Launch_Broker()
    {
        var installer = InstallerScript();

        Assert.Contains("AppId={#MyAppName}", installer);
        Assert.Contains("DefaultDirName={autopf}\\{#MyAppName}", installer);
        Assert.Contains("PrivilegesRequiredOverridesAllowed=dialog", installer);
        Assert.DoesNotContain("PrivilegesRequired=lowest", installer);
        Assert.Contains("SetupIconFile=src\\PathHide\\icon.ico", installer);
        Assert.Contains("Uninstallable=yes", installer);
        Assert.Contains("runasoriginaluser", installer);
        Assert.DoesNotContain("runascurrentuser", installer);
        Assert.Contains("Check: not IsAdminInstallMode", installer);
    }

    [Fact]
    public void Mac_Bundle_Excludes_Debug_Symbols_Before_Signing()
    {
        var targets = BuildTargets();
        var publishedFiles = targets.Descendants("_PublishedFile").Single();
        var exclusions = ((string?)publishedFiles.Attribute("Exclude") ?? string.Empty).Split(';');

        Assert.Contains("$(PublishDir)**/*.pdb", exclusions);
    }

    private static XDocument BuildTargets([CallerFilePath] string callerPath = "")
    {
        var testsProjectDir = Path.GetDirectoryName(callerPath)!;
        var repoRoot = Path.GetFullPath(Path.Combine(testsProjectDir, "..", ".."));
        return XDocument.Load(Path.Combine(repoRoot, "Directory.Build.targets"));
    }
}
