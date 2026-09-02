using System.Collections.Generic;
using System.Linq;

namespace PathHide.Storage;

/// <summary>One store that was found unreadable and set aside.</summary>
public readonly record struct QuarantinedStore(string Label, string Path);

/// <summary>
/// Stores set aside because they could not be read, held until something can
/// tell the user.
/// </summary>
/// <remarks>
/// A load can happen before the window exists (startup) or long after it
/// (Reload), and only the second can raise a dialog itself — hence a journal
/// rather than a direct report. Both drain sites word the notice through
/// <see cref="Describe"/>, so neither can describe the wrong store's contents.
/// </remarks>
public static class QuarantineJournal
{
    private static readonly List<QuarantinedStore> Entries = [];

    public static void Record(string label, string quarantinePath) =>
        Entries.Add(new QuarantinedStore(label, quarantinePath));

    public static IReadOnlyList<QuarantinedStore> Drain()
    {
        var drained = Entries.ToArray();
        Entries.Clear();
        return drained;
    }

    /// <summary>
    /// The recovery notice for a set of quarantined stores, naming which store
    /// was reset. The wording used to be hardcoded for the path list, so a
    /// quarantined settings file told the user their hidden-path list was in a
    /// file that does not contain it.
    /// </summary>
    public static (string Title, string Body) Describe(
        IReadOnlyList<QuarantinedStore> quarantined)
    {
        var labels = quarantined.Select(q => q.Label).Distinct().ToArray();
        var what = labels.Length == 1 ? $"The {labels[0]} file" : "Some files";
        var title = labels.Length == 1
            ? $"The {labels[0]} file was reset"
            : "Some files were reset";

        var body =
            $"{what} could not be read, so PathHide preserved it rather than overwriting it. "
            + "PathHide started with defaults in its place. Check the session log for the preserved "
            + "copy's location.";

        return (title, body);
    }
}
