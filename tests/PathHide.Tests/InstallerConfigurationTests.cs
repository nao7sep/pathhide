using System.IO;
using System.Runtime.CompilerServices;
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
}
