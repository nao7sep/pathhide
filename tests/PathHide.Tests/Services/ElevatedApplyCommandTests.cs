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

    // Each path travels in the list of what its write does: Hidden + System hiding sets System, and
    // any other write clears it only where PathHide set it.
    [Fact]
    public void Partition_SortsEachPathByItsVisibilityTheHideModeAndWhoSetSystem()
    {
        (string, DesiredVisibility, bool)[] targets =
        [
            ("/hide-mine", DesiredVisibility.Hidden, true),
            ("/hide-theirs", DesiredVisibility.Hidden, false),
            ("/show-mine", DesiredVisibility.Shown, true),
            ("/show-theirs", DesiredVisibility.Shown, false),
        ];

        var plain = ElevatedApplyCommand.Partition(targets, WindowsHideMode.HiddenOnly);
        Assert.Equal(["/hide-theirs"], plain.ToHide);
        Assert.Equal(["/hide-mine"], plain.ToHideClearingSystem);
        Assert.Empty(plain.ToHideWithSystem);
        Assert.Equal(["/show-theirs"], plain.ToShow);
        Assert.Equal(["/show-mine"], plain.ToShowClearingSystem);

        var withSystem = ElevatedApplyCommand.Partition(targets, WindowsHideMode.HiddenAndSystem);
        Assert.Equal(["/hide-mine", "/hide-theirs"], withSystem.ToHideWithSystem);
        Assert.Empty(withSystem.ToHide);
        Assert.Empty(withSystem.ToHideClearingSystem);
        Assert.Equal(["/show-theirs"], withSystem.ToShow);
        Assert.Equal(["/show-mine"], withSystem.ToShowClearingSystem);
        Assert.Equal(4, withSystem.Count);
    }

    [Fact]
    public void Partition_WithNothingToDo_ReturnsEmptyLists()
    {
        var buckets = ElevatedApplyCommand.Partition(
            Array.Empty<(string, DesiredVisibility, bool)>(), WindowsHideMode.HiddenAndSystem);

        Assert.Equal(0, buckets.Count);
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

    [Fact]
    public void Gui_process_identity_round_trips_without_locale_dependent_formatting()
    {
        var parent = ElevatedApplyCommand.Parent.Current;
        var invocation = ElevatedApplyCommand.ParseArguments(
            ElevatedApplyCommand.BuildArguments("/request", "/results", StorageRootArg, parent, new string('a', 64)));
        Assert.Equal(parent, invocation!.Parent);
    }

    // A launch by the GUI names its parent and carries the digest the approval was given for; one
    // without a digest, or with a malformed one, is not a valid launch.
    [Fact]
    public void A_GUI_launch_must_carry_a_well_formed_request_digest()
    {
        var parent = ElevatedApplyCommand.Parent.Current;
        var digest = ElevatedApplyCommand.RequestDigest([1, 2, 3]);

        Assert.Equal(digest, ElevatedApplyCommand.ParseArguments(
            ElevatedApplyCommand.BuildArguments("/request", "/results", StorageRootArg, parent, digest))!.RequestDigest);
        Assert.Null(ElevatedApplyCommand.ParseArguments(
            ElevatedApplyCommand.BuildArguments("/request", "/results", StorageRootArg, parent)));
        Assert.Null(ElevatedApplyCommand.ParseArguments(
            ElevatedApplyCommand.BuildArguments("/request", "/results", StorageRootArg, parent, "not-a-digest")));
    }

    [Fact]
    public void The_launching_session_round_trips_and_a_malformed_one_is_not_a_launch()
    {
        var session = PathHide.Storage.FileTimestamp.SerializedStamp(new DateTimeOffset(2026, 10, 23, 1, 2, 3, 456, TimeSpan.Zero));

        var invocation = ElevatedApplyCommand.ParseArguments(
            ElevatedApplyCommand.BuildArguments("/request", "/results", StorageRootArg, session: session));

        Assert.Equal(session, PathHide.Storage.FileTimestamp.SerializedStamp(invocation!.Session!.Value));
        Assert.Null(ElevatedApplyCommand.ParseArguments(
            ElevatedApplyCommand.BuildArguments("/request", "/results", StorageRootArg, session: "yesterday")));
    }

    [Fact]
    public void A_request_is_read_only_when_it_matches_the_approved_digest()
    {
        var content = System.Text.Encoding.UTF8.GetBytes(
            ElevatedApplyCommand.SerializeRequest(new(["/a"], [], [])));
        var approved = ElevatedApplyCommand.RequestDigest(content);

        Assert.Equal(["/a"], ElevatedApplyCommand.ReadRequest(content, approved).ToHide);
        var changed = System.Text.Encoding.UTF8.GetBytes(
            ElevatedApplyCommand.SerializeRequest(new(["/b"], [], [])));
        Assert.Throws<System.IO.InvalidDataException>(() => ElevatedApplyCommand.ReadRequest(changed, approved));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("0:1")]
    [InlineData("1:0")]
    [InlineData("1:9999999999999999999")]
    [InlineData("1:1:1")]
    [InlineData("not-a-process")]
    public void Malformed_parent_never_falls_back_to_standalone_apply(string parent) =>
        Assert.Null(ElevatedApplyCommand.ParseArguments(["apply", "--request", "/r.json", "--parent", parent]));

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
            [@"C:\quote""d\trailing\"])
        {
            ToHideClearingSystem = [@"D:\mine"],
            ToShowClearingSystem = [@"D:\also mine"],
        };

        var parsed = ElevatedApplyCommand.ParseRequest(ElevatedApplyCommand.SerializeRequest(buckets));

        Assert.Equal(buckets.ToHide, parsed.ToHide);
        Assert.Equal(buckets.ToHideWithSystem, parsed.ToHideWithSystem);
        Assert.Equal(buckets.ToShow, parsed.ToShow);
        Assert.Equal(buckets.ToHideClearingSystem, parsed.ToHideClearingSystem);
        Assert.Equal(buckets.ToShowClearingSystem, parsed.ToShowClearingSystem);
    }

    [Fact]
    public void Request_WithAMissingList_ReadsItAsEmpty()
    {
        var parsed = ElevatedApplyCommand.ParseRequest("{\"toHide\":[\"C:\\\\a\"]}");

        Assert.Equal([@"C:\a"], parsed.ToHide);
        Assert.Equal(1, parsed.Count);
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
