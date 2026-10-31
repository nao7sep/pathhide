using System;
using System.Collections.Generic;
using System.Linq;

namespace PathHide.Storage;

/// <summary>Why launch refused a file.</summary>
public enum RefusalReason
{
    /// <summary>A newer PathHide wrote it.</summary>
    Newer,

    /// <summary>It could be opened, but its content is not what PathHide saves.</summary>
    Unreadable,

    /// <summary>It could not be opened or read at all.</summary>
    Inaccessible,
}

/// <summary>One file launch refused, named by its path.</summary>
public sealed record StartupRefusal(string Path, RefusalReason Reason)
{
    /// <summary>The refusal a launch read's failure stands for, or null when it is not a refusal.</summary>
    internal static StartupRefusal? From(Exception error) => error switch
    {
        NewerFormatException newer => new(newer.Path, RefusalReason.Newer),
        UnreadableStoreException unreadable =>
            new(unreadable.Path, unreadable.IsAccessFailure ? RefusalReason.Inaccessible : RefusalReason.Unreadable),
        _ => null,
    };
}

/// <summary>
/// Launch found files it must not start with: any newer file, and unreadable settings or path list
/// (store-recovery-conventions, What a refused file does). Every one found is named, and nothing has
/// been written.
/// </summary>
public sealed class StartupRefusedException(IReadOnlyList<StartupRefusal> refusals)
    : Exception("Launch refused " + string.Join(", ", refusals.Select(refusal => $"{refusal.Path} ({refusal.Reason})")))
{
    public IReadOnlyList<StartupRefusal> Refusals { get; } = refusals;
}
