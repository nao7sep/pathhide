using System;
using System.Linq;
using PathHide.Models;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

/// <summary>
/// The parent side of the elevated-apply CLI contract: the arguments the unelevated launcher hands
/// the elevated child. The child (Program apply-mode) parses these same option names from the shared
/// <see cref="ElevatedApplyCommand"/> constants, so pinning the build here pins both halves.
/// </summary>
public sealed class ElevatedApplyCommandTests
{
    private const string StorageRootArg = @"C:\Users\u\.pathhide";

    /// <summary>
    /// The routing half of the Windows attribute rule: what a desired visibility plus the hide
    /// mode means for the Hidden and System bits, decided once for a whole batch. It used to be
    /// three inline Where clauses in the apply pass, where nothing tested it.
    /// </summary>
    [Fact]
    public void Partition_SortsEachPathByItsDesiredVisibilityAndTheHideMode()
    {
        var targets = new[]
        {
            ("/hide-me", DesiredVisibility.Hidden),
            ("/show-me", DesiredVisibility.Shown),
            ("/hide-me-too", DesiredVisibility.Hidden),
        };

        var plain = ElevatedApplyCommand.Partition(targets, WindowsHideMode.HiddenOnly);
        Assert.Equal(new[] { "/hide-me", "/hide-me-too" }, plain.ToHide);
        Assert.Empty(plain.ToHideWithSystem);
        Assert.Equal(new[] { "/show-me" }, plain.ToShow);

        // The mode moves the hides to the System list and must leave the show exactly where it is:
        // showing always clears both bits, whatever the hide mode says.
        var withSystem = ElevatedApplyCommand.Partition(targets, WindowsHideMode.HiddenAndSystem);
        Assert.Empty(withSystem.ToHide);
        Assert.Equal(new[] { "/hide-me", "/hide-me-too" }, withSystem.ToHideWithSystem);
        Assert.Equal(new[] { "/show-me" }, withSystem.ToShow);
    }

    [Fact]
    public void Partition_WithNothingToDo_ReturnsThreeEmptyLists()
    {
        var buckets = ElevatedApplyCommand.Partition(
            Array.Empty<(string, DesiredVisibility)>(), WindowsHideMode.HiddenAndSystem);

        Assert.Empty(buckets.ToHide);
        Assert.Empty(buckets.ToHideWithSystem);
        Assert.Empty(buckets.ToShow);
    }

    [Fact]
    public void BuildArguments_CarriesOnlyTheFilesAndTheRoot()
    {
        var args = ElevatedApplyCommand.BuildArguments(@"C:\T\req.json", @"C:\T\res.jsonl", StorageRootArg);

        // The root travels as an argument because the runas verb forbids setting the child's
        // environment, so a relocated PATHHIDE_DATA_DIR would not reach it.
        Assert.Equal(
            new[]
            {
                ElevatedApplyCommand.Subcommand,
                ElevatedApplyCommand.RequestOption, @"C:\T\req.json",
                ElevatedApplyCommand.ResultsOption, @"C:\T\res.jsonl",
                ElevatedApplyCommand.HomeOption, StorageRootArg,
            },
            args);
    }

    [Fact]
    public void ParseArguments_ReadsBackWhatBuildArgumentsWrote()
    {
        var request = @"C:\Users\山田 太郎\AppData\Local\Temp\pathhide-apply-PC.1.a.request.json";
        var results = @"C:\Users\山田 太郎\AppData\Local\Temp\pathhide-apply-PC.1.a.results.jsonl";
        var root = @"D:\Data Root\";

        var invocation = ElevatedApplyCommand.ParseArguments(
            ElevatedApplyCommand.BuildArguments(request, results, root));

        Assert.Equal(new ElevatedApplyCommand.Invocation(request, results, root), invocation);
    }

    [Fact]
    public void ParseArguments_WithoutResultsOrRoot_StillRuns()
    {
        var invocation = ElevatedApplyCommand.ParseArguments(
            [ElevatedApplyCommand.Subcommand, ElevatedApplyCommand.RequestOption, "/r.json"]);

        Assert.Equal(new ElevatedApplyCommand.Invocation("/r.json", null, null), invocation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("apply")]
    [InlineData("apply|--results|/r.jsonl")]
    [InlineData("apply|--request|/r.json|--hide|C:\\x")]
    [InlineData("other|--request|/r.json")]
    public void ParseArguments_RejectsAnythingButAnApplyWithARequest(string joined) =>
        Assert.Null(ElevatedApplyCommand.ParseArguments(joined.Split('|', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void Request_RoundTripsPathsWithSpacesNonAsciiAndADriveRoot()
    {
        var buckets = new ElevatedApplyCommand.Buckets(
            [@"C:\Program Files\a b", @"C:\"],
            [@"\\server\share\日本語 フォルダ"],
            [@"C:\quote""d\trailing\"]);

        var parsed = ElevatedApplyCommand.ParseRequest(ElevatedApplyCommand.SerializeRequest(buckets));

        Assert.Equal(buckets.ToHide, parsed.ToHide);
        Assert.Equal(buckets.ToHideWithSystem, parsed.ToHideWithSystem);
        Assert.Equal(buckets.ToShow, parsed.ToShow);
    }

    [Fact]
    public void Request_WithAMissingList_ReadsItAsEmpty()
    {
        var parsed = ElevatedApplyCommand.ParseRequest("{\"toHide\":[\"C:\\\\a\"]}");

        Assert.Equal([@"C:\a"], parsed.ToHide);
        Assert.Empty(parsed.ToHideWithSystem);
        Assert.Empty(parsed.ToShow);
    }

    /// <summary>
    /// Windows caps a command line at 32,767 characters. The paths travel in the request file, so a
    /// batch far past that cap still launches with a short command line and arrives whole.
    /// </summary>
    [Fact]
    public void ALargeBatch_KeepsTheCommandLineShortAndTheRequestWhole()
    {
        var paths = Enumerable.Range(0, 1000)
            .Select(i => $@"C:\Users\u\{new string('x', 200)}\{i}")
            .ToList();
        var buckets = new ElevatedApplyCommand.Buckets(paths, [], []);

        var args = ElevatedApplyCommand.BuildArguments(@"C:\T\req.json", @"C:\T\res.jsonl", StorageRootArg);
        Assert.True(args.Sum(arg => arg.Length + 3) < 1_000);
        Assert.True(paths.Sum(path => path.Length) > 32_767);

        Assert.Equal(paths, ElevatedApplyCommand.ParseRequest(ElevatedApplyCommand.SerializeRequest(buckets)).ToHide);
    }
}
