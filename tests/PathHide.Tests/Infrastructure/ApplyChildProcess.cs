using System;
using System.Diagnostics;
using PathHide.Services;
using PathHide.Storage;
using Xunit;

namespace PathHide.Tests.Infrastructure;

/// <summary>Runs only the app's non-GUI apply mode and owns the process through exit, even on failure.</summary>
internal static class ApplyChildProcess
{
    internal static int Run(string request, string results, string root, ElevatedApplyCommand.Parent? parent = null)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.Environment[StorageRoot.DataDirEnvironmentVariable] = root;
        start.ArgumentList.Add(typeof(App).Assembly.Location);
        foreach (var argument in ElevatedApplyCommand.BuildArguments(request, results, root, parent))
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
