using System.Collections.Generic;
using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using PathHide.Models;

namespace PathHide.Services;

/// <summary>
/// The contract between the unelevated parent and the elevated <c>apply</c> child: the subcommand, its
/// options, and the request file that carries the path lists. Both sides go through this type — the
/// parent (<see cref="ElevatedApplicator"/>) when it writes the request and builds the arguments, and
/// the child (<c>Program</c> apply-mode) when it parses them — so the two halves cannot drift. The
/// per-path outcomes travel back via <see cref="ElevatedApplyResults"/>.
/// </summary>
/// <remarks>
/// The paths travel in a file, not on the command line: Windows caps a command line at 32,767
/// characters, so a Hide All over a few hundred access-denied paths could not launch at all, and every
/// retry of it failed the same way.
/// </remarks>
public static class ElevatedApplyCommand
{
    public const string Subcommand = "apply";
    public const string RequestOption = "--request";
    public const string ResultsOption = "--results";

    /// <summary>
    /// The storage root the parent resolved, so the child logs into the same tree.
    /// </summary>
    /// <remarks>
    /// It has to travel as an argument. The runas verb forces UseShellExecute, which forbids
    /// setting the child's environment block, so a root relocated by PATHHIDE_HOME would not
    /// reach it — the child would re-resolve to the default and split the log trail for exactly
    /// the access-denied failures this pass exists to diagnose.
    /// </remarks>
    public const string HomeOption = "--home";

    /// <summary>The three path lists the elevated child takes, in the order it takes them.</summary>
    public sealed record Buckets(
        IReadOnlyList<string> ToHide,
        IReadOnlyList<string> ToHideWithSystem,
        IReadOnlyList<string> ToShow);

    /// <summary>
    /// Sorts the paths that need an elevated retry into the child's three lists.
    /// </summary>
    /// <remarks>
    /// This is the third and last spelling of one rule — what a desired visibility plus the
    /// Windows hide mode means for the Hidden and System bits. <see cref="WindowsFileVisibility"/>
    /// writes it as bit math for the two processes that touch a file; here it decides which list
    /// a path travels in, which is the same decision made once for a whole batch. It lived inline
    /// in the apply pass as three <c>Where</c> clauses, where nothing could test it and nothing
    /// tied it to the rule it was restating.
    /// </remarks>
    public static Buckets Partition(
        IEnumerable<(string Path, DesiredVisibility Desired)> targets,
        WindowsHideMode mode)
    {
        var toHide = new List<string>();
        var toHideWithSystem = new List<string>();
        var toShow = new List<string>();

        foreach (var (path, desired) in targets)
        {
            if (desired == DesiredVisibility.Shown)
                toShow.Add(path);
            else if (mode == WindowsHideMode.HiddenAndSystem)
                toHideWithSystem.Add(path);
            else
                toHide.Add(path);
        }

        return new Buckets(toHide, toHideWithSystem, toShow);
    }

    /// <summary>
    /// Builds the argument list that launches the elevated apply pass. Its length does not grow with
    /// the batch: the paths are in the request file.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string requestPath, string resultsPath, string storageRoot) =>
        [Subcommand, RequestOption, requestPath, ResultsOption, resultsPath, HomeOption, storageRoot];

    /// <summary>What the elevated child was asked to do, as parsed from its command line.</summary>
    public sealed record Invocation(string RequestPath, string? ResultsPath, string? StorageRoot);

    /// <summary>
    /// Parses the child's command line, or returns null when it is not a valid apply invocation. The
    /// results file is optional (a standalone run writes none); the request file is required.
    /// </summary>
    public static Invocation? ParseArguments(IReadOnlyList<string> args)
    {
        var requestOpt = new Option<string>(RequestOption) { Required = true };
        var resultsOpt = new Option<string?>(ResultsOption);
        var homeOpt = new Option<string?>(HomeOption);
        var applyCmd = new Command(Subcommand, "Apply file attributes in batch") { requestOpt, resultsOpt, homeOpt };
        var root = new RootCommand("PathHide apply mode") { applyCmd };

        var result = root.Parse(args);
        if (result.Errors.Count > 0 || result.CommandResult.Command != applyCmd)
            return null;

        var request = result.GetValue(requestOpt);
        return string.IsNullOrEmpty(request)
            ? null
            : new Invocation(request, result.GetValue(resultsOpt), result.GetValue(homeOpt));
    }

    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The request file's content: the three path lists.</summary>
    public static string SerializeRequest(Buckets buckets) => JsonSerializer.Serialize(buckets, RequestOptions);

    /// <summary>Reads a request file's content back into the three lists; a missing list is empty.</summary>
    public static Buckets ParseRequest(string json)
    {
        var parsed = JsonSerializer.Deserialize<Buckets>(json, RequestOptions)
            ?? throw new JsonException("The apply request is empty.");
        return new Buckets(parsed.ToHide ?? [], parsed.ToHideWithSystem ?? [], parsed.ToShow ?? []);
    }
}
