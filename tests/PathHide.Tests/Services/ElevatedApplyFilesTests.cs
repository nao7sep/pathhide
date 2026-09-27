using System;
using System.IO;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

/// <summary>
/// The elevated apply's temp files: their names record their owner, and the launch sweep deletes only
/// files this host wrote from a process that has exited.
/// </summary>
public sealed class ElevatedApplyFilesTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("pathhide-tests-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void ANewPair_RecordsThisHostAndThisProcess()
    {
        var files = ElevatedApplyFiles.Create(_temp);

        Assert.StartsWith("pathhide-apply-", Path.GetFileName(files.ResultsPath));
        Assert.EndsWith(".jsonl", files.ResultsPath);
        Assert.True(ElevatedApplyFiles.TryReadOwner(Path.GetFileName(files.RequestPath), out _, out var requestOwner));
        Assert.True(ElevatedApplyFiles.TryReadOwner(Path.GetFileName(files.ResultsPath), out _, out var resultsOwner));
        Assert.Equal(Environment.ProcessId, requestOwner);
        Assert.Equal(Environment.ProcessId, resultsOwner);
    }

    [Theory]
    [InlineData("pathhide-apply-.1.a.results.jsonl")]
    [InlineData("pathhide-apply-PC.x.a.results.jsonl")]
    [InlineData("pathhide-apply-PC.1.a.results.txt")]
    [InlineData("pathhide-apply-oldstyle.jsonl")]
    [InlineData("other-PC.1.a.results.jsonl")]
    public void ANameThatDoesNotRecordAnOwner_IsNotOneOfOurs(string fileName) =>
        Assert.False(ElevatedApplyFiles.TryReadOwner(fileName, out _, out _));

    [Fact]
    public void TheSweep_DeletesOnlyThisHostsFilesWhoseProcessHasExited()
    {
        var dead = ElevatedApplyFiles.Create(_temp);
        var live = ElevatedApplyFiles.Create(_temp);
        foreach (var path in new[] { dead.RequestPath, dead.ResultsPath, live.RequestPath, live.ResultsPath })
            File.WriteAllText(path, "{}");
        // Another host's file in a shared directory proves nothing about its process.
        var otherHost = Path.Combine(_temp, "pathhide-apply-SOMEOTHERHOST.1.a.results.jsonl");
        var unrelated = Path.Combine(_temp, "pathhide-apply-notes.txt");
        File.WriteAllText(otherHost, "{}");
        File.WriteAllText(unrelated, "{}");

        var deadPid = ReadOwner(dead.ResultsPath) + 1_000_000;
        File.Move(dead.RequestPath, Rename(dead.RequestPath, deadPid));
        File.Move(dead.ResultsPath, Rename(dead.ResultsPath, deadPid));

        var deleted = ElevatedApplyFiles.SweepLeftovers(_temp, pid => pid != deadPid);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(Rename(dead.RequestPath, deadPid)));
        Assert.False(File.Exists(Rename(dead.ResultsPath, deadPid)));
        Assert.True(File.Exists(live.RequestPath));
        Assert.True(File.Exists(live.ResultsPath));
        Assert.True(File.Exists(otherHost));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void ReadResults_SeesWhatAStillOpenWriterHasFlushed()
    {
        var files = ElevatedApplyFiles.Create(_temp);
        using (var writer = new StreamWriter(new FileStream(files.ResultsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)))
        {
            writer.Write(ElevatedApplyResults.SerializeLine(new PathApplyResult(@"C:\a", Ok: true)));
            writer.Flush();

            var results = files.ReadResults();

            Assert.True(results[@"C:\a"]);
        }

        Assert.True(files.TryDelete());
        Assert.False(File.Exists(files.ResultsPath));
    }

    private static int ReadOwner(string path)
    {
        Assert.True(ElevatedApplyFiles.TryReadOwner(Path.GetFileName(path), out _, out var pid));
        return pid;
    }

    private static string Rename(string path, int pid) =>
        Path.Combine(Path.GetDirectoryName(path)!,
            Path.GetFileName(path).Replace($".{Environment.ProcessId}.", $".{pid}."));
}
