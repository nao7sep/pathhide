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
    private const string StorageRootArg = @"C:\Users\u\.pathhide";

    [Fact]
    public void BuildArguments_PutsTheSubcommandFirstAndTheFixedOptionsLast()
    {
        var args = ElevatedApplyCommand.BuildArguments(
            new[] { "/a" }, new[] { "/b" }, new[] { "/c" }, "/tmp/results.jsonl", StorageRootArg);

        Assert.Equal(ElevatedApplyCommand.Subcommand, args[0]);
        Assert.Equal(ElevatedApplyCommand.ResultsOption, args[^4]);
        Assert.Equal("/tmp/results.jsonl", args[^3]);
        // The root travels as an argument because the runas verb forbids setting the child's
        // environment, so a relocated PATHHIDE_HOME would not reach it.
        Assert.Equal(ElevatedApplyCommand.HomeOption, args[^2]);
        Assert.Equal(StorageRootArg, args[^1]);
    }

    [Fact]
    public void BuildArguments_GroupsEachPathListUnderItsOption()
    {
        var args = ElevatedApplyCommand.BuildArguments(
            new[] { "/h1", "/h2" }, new[] { "/s1" }, new[] { "/w1" }, "/r", StorageRootArg);

        Assert.Equal(
            new[]
            {
                ElevatedApplyCommand.Subcommand,
                ElevatedApplyCommand.HideOption, "/h1", "/h2",
                ElevatedApplyCommand.SystemOption, "/s1",
                ElevatedApplyCommand.ShowOption, "/w1",
                ElevatedApplyCommand.ResultsOption, "/r",
                ElevatedApplyCommand.HomeOption, StorageRootArg,
            },
            args);
    }

    [Fact]
    public void BuildArguments_OmitsEmptyLists_MatchingTheChildsZeroOrMoreArity()
    {
        // Only Hide carries paths: the System and Show options must not appear at all.
        var args = ElevatedApplyCommand.BuildArguments(
            new[] { "/h" }, Array.Empty<string>(), Array.Empty<string>(), "/r", StorageRootArg);

        Assert.DoesNotContain(ElevatedApplyCommand.SystemOption, args);
        Assert.DoesNotContain(ElevatedApplyCommand.ShowOption, args);
        Assert.Equal(
            new[]
            {
                ElevatedApplyCommand.Subcommand,
                ElevatedApplyCommand.HideOption, "/h",
                ElevatedApplyCommand.ResultsOption, "/r",
                ElevatedApplyCommand.HomeOption, StorageRootArg,
            },
            args);
    }

    [Fact]
    public void BuildArguments_WithNoPaths_IsJustTheSubcommandAndTheFixedOptions()
    {
        var args = ElevatedApplyCommand.BuildArguments(
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "/r", StorageRootArg);

        Assert.Equal(
            new[]
            {
                ElevatedApplyCommand.Subcommand,
                ElevatedApplyCommand.ResultsOption, "/r",
                ElevatedApplyCommand.HomeOption, StorageRootArg,
            },
            args);
    }
}
