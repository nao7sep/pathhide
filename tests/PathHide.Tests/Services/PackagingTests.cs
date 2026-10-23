using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xunit;

namespace PathHide.Tests.Services;

public sealed class PackagingTests
{
    [WindowsOnlyFact(Timeout = 30_000)]
    public async Task Native_failures_and_missing_artifacts_fail_packaging()
    {
        foreach (var stage in new[] { "property", "publish", "installer", "missing" })
        {
            var root = Directory.CreateTempSubdirectory("pathhide-package-").FullName;
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "scripts"));
                File.Copy(SourceScript(), Path.Combine(root, "scripts", "package.ps1"));
                File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project><Version>1.0.0</Version></Project>");
                File.WriteAllText(Path.Combine(root, "LICENSE"), "Fixture license");
                File.WriteAllText(Path.Combine(root, "dotnet.cmd"), """
                    @echo off
                    if "%1"=="publish" goto publish
                    if "%FAIL_STAGE%"=="property" exit /b 17
                    if "%3"=="-getProperty:RuntimeIdentifiers" (echo win-x64) else (echo Fixture)
                    exit /b 0
                    :publish
                    if "%FAIL_STAGE%"=="publish" exit /b 18
                    mkdir publish-win
                    echo fixture>publish-win\Fixture.exe
                    exit /b 0
                    """);
                File.WriteAllText(Path.Combine(root, "iscc.cmd"), """
                    @echo off
                    if "%FAIL_STAGE%"=="installer" exit /b 19
                    exit /b 0
                    """);
                var start = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = root,
                };
                foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "scripts", "package.ps1") })
                    start.ArgumentList.Add(argument);
                start.Environment["PATH"] = root + Path.PathSeparator + start.Environment["PATH"];
                start.Environment["FAIL_STAGE"] = stage;
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.NotEqual(0, process.ExitCode);
                var message = await error + await output;
                Assert.Contains(stage switch
                {
                    "property" => "exit code 17", "publish" => "exit code 18",
                    "installer" => "exit code 19", _ => "Packaging did not produce",
                }, message);
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static string SourceScript([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../../scripts/package.ps1"));
}
