using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

public sealed class LauncherRuntimeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pathhide-launcher-").FullName;
    public LauncherRuntimeTests() => Directory.CreateDirectory(Path.Combine(_root, ".git"));
    public void Dispose() => Directory.Delete(_root, true);
    private string Record => Path.Combine(_root, ".git", "launcher-runtime-child");

    [Fact]
    public void Only_the_current_launcher_receives_a_process_report()
    {
        File.WriteAllText(Path.Combine(_root, ".git", "launcher-runtime-owner"), "current");
        LauncherRuntime.Report(["--launcher-repo", _root, "--launcher-token", "old"], false);
        Assert.False(File.Exists(Record));
        LauncherRuntime.Report(["--launcher-repo", _root, "--launcher-token", "current"], true);
        using var record = JsonDocument.Parse(File.ReadAllBytes(Record));
        Assert.Equal(Environment.ProcessId, record.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal("activated", record.RootElement.GetProperty("state").GetString());
    }

    [MacOnlyFact(Timeout = 15_000)]
    public async Task Replacement_leaves_unrecorded_processes_and_reused_identities_alone()
    {
        using var owned = Process.Start(new ProcessStartInfo("/bin/sleep", "60") { UseShellExecute = false })!;
        using var manual = Process.Start(new ProcessStartInfo("/bin/sleep", "60") { UseShellExecute = false })!;
        try
        {
            void RecordChild(string label) => File.WriteAllText(Record, JsonSerializer.Serialize(new
            { pid = owned.Id, startLabel = label, state = "running", token = "old" }));
            RecordChild("a different process creation time");
            await Stop();
            Assert.False(owned.HasExited);
            Assert.False(manual.HasExited);
            RecordChild(owned.StartTime.ToUniversalTime().ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture));
            await Stop();
            await owned.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.False(manual.HasExited);
        }
        finally
        {
            if (!owned.HasExited) owned.Kill();
            if (!manual.HasExited) manual.Kill();
            await Task.WhenAll(owned.WaitForExitAsync(TestContext.Current.CancellationToken), manual.WaitForExitAsync(TestContext.Current.CancellationToken));
        }
    }

    private async Task Stop([CallerFilePath] string source = "")
    {
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../.."));
        var script = Path.Combine(_root, "stop.sh");
        // The fixture owns these console children. Production requests an AppKit close, never a kill.
        File.WriteAllText(script, "source \"$1\"\nrequest_runtime_close() { kill -TERM \"$1\"; }\nstop_owned_runtime Fixture \"$2\"\n");
        var start = new ProcessStartInfo("/bin/bash") { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(Path.Combine(repo, "scripts", "launcher-runtime.sh"));
        start.ArgumentList.Add(_root);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
    }
}
