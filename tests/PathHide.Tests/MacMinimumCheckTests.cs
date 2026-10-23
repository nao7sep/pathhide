using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace PathHide.Tests;

/// <summary>The bundle check that the declared minimum macOS covers every shipped binary.</summary>
public sealed class MacMinimumCheckTests
{
    private static string RepoRoot([CallerFilePath] string callerPath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", ".."));

    // A bundle with no binary to read a minimum from is reported, not passed in silence.
    [MacOnlyFact]
    public void A_bundle_without_a_binary_is_reported()
    {
        var bundle = Directory.CreateTempSubdirectory("pathhide-minimum-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(bundle, "Contents"));
            File.Copy(Path.Combine(RepoRoot(), "macOS", "Info.plist"), Path.Combine(bundle, "Contents", "Info.plist"));

            using var check = Process.Start(new ProcessStartInfo("/bin/bash",
                [Path.Combine(RepoRoot(), "scripts", "check-macos-minimum.sh"), bundle])
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            var error = check.StandardError.ReadToEnd();
            Assert.True(check.WaitForExit(TimeSpan.FromSeconds(30)), "The check did not finish.");

            Assert.Equal(1, check.ExitCode);
            Assert.Contains("No Mach-O minimum was found", error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }
}
