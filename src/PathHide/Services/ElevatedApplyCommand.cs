using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PathHide.Models;

namespace PathHide.Services;

/// <summary>
/// The contract between the unelevated parent and the elevated <c>apply</c> child: the subcommand, its
/// options, and the request file that carries the path lists. Both sides go through this type — the
/// parent (<see cref="ElevatedApplicator"/>) when it writes the request and builds the arguments, and
/// the child (<c>Program</c> apply-mode) when it parses them — so the two halves cannot drift. The
/// per-path outcomes and the child's log entries travel back via <see cref="ElevatedApplyResults"/>.
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
    public const string ParentOption = "--parent";

    /// <summary>
    /// The SHA-256 of the request file as the GUI wrote it. The request sits in the user's temp folder,
    /// which any unelevated process of the user can change after the approval prompt; the command line
    /// is fixed when the elevated process starts, so the digest it carries is what the child trusts.
    /// </summary>
    public const string RequestDigestOption = "--request-sha256";

    /// <summary>PID plus UTC creation time distinguishes the GUI from a recycled process id.</summary>
    public sealed record Parent(int Id, long StartTimeUtcTicks)
    {
        public static Parent Current
        {
            get
            {
                using var process = Process.GetCurrentProcess();
                return new(process.Id, process.StartTime.ToUniversalTime().Ticks);
            }
        }

        public override string ToString() => FormattableString.Invariant($"{Id}:{StartTimeUtcTicks}");
    }

    /// <summary>
    /// The storage root the parent resolved, so a fallback log file the child writes lands in the
    /// same tree.
    /// </summary>
    /// <remarks>
    /// It has to travel as an argument. The runas verb forces UseShellExecute, which forbids
    /// setting the child's environment block, so a root relocated by PATHHIDE_DATA_DIR would not
    /// reach it — the child would re-resolve to the default and split the log trail for exactly
    /// the access-denied failures this pass exists to diagnose.
    /// </remarks>
    public const string HomeOption = "--home";

    /// <summary>
    /// The launch the child works for, as its records name it: entries the GUI imports from the child
    /// belong to the launch whose apply asked for them, so "this launch" shows them.
    /// </summary>
    public const string SessionOption = "--session";

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
    public static IReadOnlyList<string> BuildArguments(string requestPath, string resultsPath, string storageRoot,
        Parent? parent = null, string? requestDigest = null, string? session = null)
    {
        var arguments = new List<string> { Subcommand, RequestOption, requestPath, ResultsOption, resultsPath,
            HomeOption, storageRoot };
        if (parent is not null)
            arguments.AddRange([ParentOption, parent.ToString()]);
        if (requestDigest is not null)
            arguments.AddRange([RequestDigestOption, requestDigest]);
        if (session is not null)
            arguments.AddRange([SessionOption, session]);
        return arguments;
    }

    /// <summary>What the elevated child was asked to do, as parsed from its command line.</summary>
    public sealed record Invocation(string RequestPath, string? ResultsPath, string? StorageRoot, Parent? Parent = null,
        string? RequestDigest = null, DateTimeOffset? Session = null);

    /// <summary>
    /// Parses the child's command line, or returns null when it is not a valid apply invocation. The
    /// results file is optional (a standalone run writes none); the request file is required. A launch
    /// by the GUI, which names its parent, must also carry the request's digest.
    /// </summary>
    public static Invocation? ParseArguments(IReadOnlyList<string> args)
    {
        var requestOpt = new Option<string>(RequestOption) { Required = true };
        var resultsOpt = new Option<string?>(ResultsOption);
        var homeOpt = new Option<string?>(HomeOption);
        var parentOpt = new Option<string?>(ParentOption);
        var digestOpt = new Option<string?>(RequestDigestOption);
        var sessionOpt = new Option<string?>(SessionOption);
        var applyCmd = new Command(Subcommand, "Apply file attributes in batch")
        {
            requestOpt, resultsOpt, homeOpt, parentOpt, digestOpt, sessionOpt,
        };
        var root = new RootCommand("PathHide apply mode") { applyCmd };

        var result = root.Parse(args);
        if (result.Errors.Count > 0 || result.CommandResult.Command != applyCmd)
            return null;

        var request = result.GetValue(requestOpt);
        Parent? parent = null;
        if (result.GetValue(parentOpt) is { } parentText)
        {
            var parts = parentText.Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
                return null;
            parent = new(id, ticks);
        }
        var digest = result.GetValue(digestOpt);
        if (digest is not null && (digest.Length != 64 || !digest.All(Uri.IsHexDigit)))
            return null;
        if (parent is not null && digest is null)
            return null;
        DateTimeOffset? session = null;
        if (result.GetValue(sessionOpt) is { } sessionText
            && (session = Storage.FileTimestamp.ParseSerializedStamp(sessionText)) is null)
            return null;
        return string.IsNullOrEmpty(request)
            ? null
            : new Invocation(request, result.GetValue(resultsOpt), result.GetValue(homeOpt), parent, digest, session);
    }

    /// <summary>The SHA-256 of request content, lowercase hex, as the command line carries it.</summary>
    public static string RequestDigest(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>
    /// Reads request content the child has loaded once. When the command line carries a digest, the
    /// content must match it, or the request is refused before any path is touched.
    /// </summary>
    public static Buckets ReadRequest(byte[] content, string? expectedDigest)
    {
        if (expectedDigest is not null
            && !string.Equals(RequestDigest(content), expectedDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The request file changed after the elevated launch was approved.");
        return ParseRequest(Encoding.UTF8.GetString(content));
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
