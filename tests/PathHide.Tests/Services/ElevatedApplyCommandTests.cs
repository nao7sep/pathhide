using System;
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
    [Fact]
    public void BuildArguments_PutsTheSubcommandFirstAndTheResultsPathLast()
    {
        var args = ElevatedApplyCommand.BuildArguments(
            new[] { "/a" }, new[] { "/b" }, new[] { "/c" }, "/tmp/results.jsonl");

        Assert.Equal(ElevatedApplyCommand.Subcommand, args[0]);
        Assert.Equal(ElevatedApplyCommand.ResultsOption, args[^2]);
        Assert.Equal("/tmp/results.jsonl", args[^1]);
    }

    [Fact]
    public void BuildArguments_GroupsEachPathListUnderItsOption()
    {
        var args = ElevatedApplyCommand.BuildArguments(
            new[] { "/h1", "/h2" }, new[] { "/s1" }, new[] { "/w1" }, "/r");

        Assert.Equal(
            new[]
            {
                ElevatedApplyCommand.Subcommand,
                ElevatedApplyCommand.HideOption, "/h1", "/h2",
                ElevatedApplyCommand.SystemOption, "/s1",
                ElevatedApplyCommand.ShowOption, "/w1",
                ElevatedApplyCommand.ResultsOption, "/r",
            },
            args);
    }

    [Fact]
    public void BuildArguments_OmitsEmptyLists_MatchingTheChildsZeroOrMoreArity()
    {
        // Only Hide carries paths: the System and Show options must not appear at all.
        var args = ElevatedApplyCommand.BuildArguments(
            new[] { "/h" }, Array.Empty<string>(), Array.Empty<string>(), "/r");

        Assert.DoesNotContain(ElevatedApplyCommand.SystemOption, args);
        Assert.DoesNotContain(ElevatedApplyCommand.ShowOption, args);
        Assert.Equal(
            new[]
            {
                ElevatedApplyCommand.Subcommand,
                ElevatedApplyCommand.HideOption, "/h",
                ElevatedApplyCommand.ResultsOption, "/r",
            },
            args);
    }

    [Fact]
    public void BuildArguments_WithNoPaths_IsJustTheSubcommandAndResults()
    {
        var args = ElevatedApplyCommand.BuildArguments(
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "/r");

        Assert.Equal(
            new[] { ElevatedApplyCommand.Subcommand, ElevatedApplyCommand.ResultsOption, "/r" },
            args);
    }
}
