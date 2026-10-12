using System;
using System.IO;
using PathHide.Services;
using PathHide.Tests.Infrastructure;
using Xunit;

namespace PathHide.Tests.Services;

/// <summary>Failure admission through the real child entry point; these paths need no Windows syscall.</summary>
public sealed class ApplyModeProcessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pathhide-apply-process-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrMalformedRequest_ExitsWithFailureWithoutAReportedSuccess(bool malformed)
    {
        var request = Path.Combine(_dir, "request.json");
        var results = Path.Combine(_dir, "results.jsonl");
        var root = Path.Combine(_dir, "root");
        if (malformed)
            File.WriteAllText(request, "{ invalid json");

        Assert.Equal(3, ApplyChildProcess.Run(request, results, root));

        var report = ElevatedApplyResults.Parse(File.ReadAllText(results));
        Assert.Empty(report.Results);
        Assert.Contains(report.Entries, entry => entry.Level == "error");
        Assert.False(File.Exists(Path.Combine(root, "records.sqlite3")));
        if (malformed)
            Assert.Equal("{ invalid json", File.ReadAllText(request));
        else
            Assert.False(File.Exists(request));
    }

    [Fact]
    public void ExistingResults_AreNotOverwrittenAndNoAttributesAreApplied()
    {
        var target = Path.Combine(_dir, "target.txt");
        File.WriteAllText(target, "keep");
        var attributes = File.GetAttributes(target);
        var request = Path.Combine(_dir, "request.json");
        File.WriteAllText(request, ElevatedApplyCommand.SerializeRequest(new([target], [], [])));
        var results = Path.Combine(_dir, "results.jsonl");
        File.WriteAllText(results, "existing result");
        var root = Path.Combine(_dir, "root");

        Assert.Equal(3, ApplyChildProcess.Run(request, results, root));

        Assert.Equal("existing result", File.ReadAllText(results));
        Assert.Equal(attributes, File.GetAttributes(target));
        Assert.Equal("keep", File.ReadAllText(target));
        Assert.False(File.Exists(Path.Combine(root, "records.sqlite3")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "logs")));
    }

    [Fact]
    public void A_replaced_parent_identity_fails_before_the_child_changes_any_target()
    {
        var target = Path.Combine(_dir, "target.txt");
        File.WriteAllText(target, "keep");
        var attributes = File.GetAttributes(target);
        var request = Path.Combine(_dir, "request.json");
        var results = Path.Combine(_dir, "results.jsonl");
        File.WriteAllText(request, ElevatedApplyCommand.SerializeRequest(new([target], [], [])));
        var parent = ElevatedApplyCommand.Parent.Current;
        Assert.Equal(3, ApplyChildProcess.Run(request, results, Path.Combine(_dir, "root"), parent with
        {
            StartTimeUtcTicks = parent.StartTimeUtcTicks - 1,
        }));
        Assert.Empty(ElevatedApplyResults.Parse(File.ReadAllText(results)).Results);
        Assert.Equal(attributes, File.GetAttributes(target));
        Assert.Equal("keep", File.ReadAllText(target));
    }
}
