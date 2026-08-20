using System.Collections.Generic;
using PathHide.Models;

namespace PathHide.Services;

/// <summary>
/// The command-line contract between the unelevated parent and the elevated <c>apply</c> child: the
/// subcommand name, the three path-list option names, and the results-file option. Both sides reference
/// these constants — the parent (<see cref="WindowsElevatedApplicator"/>) when it builds the arguments to
/// launch the child, and the child (<c>Program</c> apply-mode) when it parses them — so the two halves of
/// the contract cannot drift. The per-path outcomes travel back via <see cref="ElevatedApplyResults"/>.
/// </summary>
public static class ElevatedApplyCommand
{
    public const string Subcommand = "apply";
    public const string HideOption = "--hide";
    public const string SystemOption = "--system";
    public const string ShowOption = "--show";
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
    /// Builds the argument list that launches the elevated apply pass: the subcommand first, then each
    /// non-empty path list under its option (a list is omitted entirely when empty, matching the child's
    /// <c>ZeroOrMore</c> arity), then the results-file path and the storage root. Pure, so the wiring is
    /// unit-tested without spawning a process.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(
        IReadOnlyList<string> toHide,
        IReadOnlyList<string> toHideWithSystem,
        IReadOnlyList<string> toShow,
        string resultsPath,
        string storageRoot)
    {
        var args = new List<string> { Subcommand };
        AppendOption(args, HideOption, toHide);
        AppendOption(args, SystemOption, toHideWithSystem);
        AppendOption(args, ShowOption, toShow);
        args.Add(ResultsOption);
        args.Add(resultsPath);
        args.Add(HomeOption);
        args.Add(storageRoot);
        return args;
    }

    private static void AppendOption(List<string> args, string option, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        args.Add(option);
        args.AddRange(paths);
    }
}
