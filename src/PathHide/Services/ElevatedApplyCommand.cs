using System.Collections.Generic;

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
