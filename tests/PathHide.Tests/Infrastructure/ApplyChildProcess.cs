using System;
using System.Diagnostics;
using PathHide.Services;
using PathHide.Storage;
using Xunit;

namespace PathHide.Tests.Infrastructure;

/// <summary>Runs only the app's non-GUI apply mode and owns the process through exit, even on failure.</summary>
internal static class ApplyChildProcess
{
    /// <remarks>
    /// With a parent, the launch carries the request's digest as the GUI's does: the current file's,
    /// unless <paramref name="requestDigest"/> names the one the launch was approved with.
    /// </remarks>
    internal static int Run(string request, string results, string root, ElevatedApplyCommand.Parent? parent = null,
        string? requestDigest = null, string? session = null)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.Environment[StorageRoot.DataDirEnvironmentVariable] = root;
        start.ArgumentList.Add(typeof(App).Assembly.Location);
        var digest = parent is null ? requestDigest
            : requestDigest ?? ElevatedApplyCommand.RequestDigest(System.IO.File.ReadAllBytes(request));
        foreach (var argument in ElevatedApplyCommand.BuildArguments(request, results, root, parent, digest, session))
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        try
        {
            Assert.True(child.WaitForExit(TimeSpan.FromSeconds(60)), "The apply child did not exit.");
            return child.ExitCode;
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }
        }
    }
}
