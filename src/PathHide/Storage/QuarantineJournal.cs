using System.Collections.Generic;
using System.Linq;
using PathHide.I18n;

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

    /// <remarks>
    /// Window state is disposable presentation state: its reset is a warning in the log, which the
    /// store has already written, and no notice to the user (storage-path conventions).
    /// </remarks>
    public static void Record(string label, string quarantinePath)
    {
        if (label != StateLabel)
            Entries.Add(new QuarantinedStore(label, quarantinePath));
    }

    public static IReadOnlyList<QuarantinedStore> Drain()
    {
        var drained = Entries.ToArray();
        Entries.Clear();
        return drained;
    }

    /// <summary>The label the path list's store is created with (see <c>App.CreateMainViewModel</c>).</summary>
    public const string PathListLabel = "paths";

    /// <summary>The label the settings store is created with.</summary>
    public const string SettingsLabel = "settings";

    /// <summary>The label the window-state store is created with.</summary>
    public const string StateLabel = "state";

    /// <summary>
    /// The recovery notice for a set of quarantined stores, naming which store was reset. The wording
    /// used to be hardcoded for the path list, so a quarantined settings file told the user their
    /// hidden-path list was in a file that does not contain it.
    /// </summary>
    /// <remarks>
    /// Each store has its own sentences rather than a name slotted into one, because a store's name
    /// changes the words around it in most languages. They also say different things: the settings
    /// file is only read at startup, which then goes on with defaults, while the path list reaches
    /// this notice only from Reload — an unreadable list at startup halts instead — and Reload keeps
    /// the entries already on screen.
    /// </remarks>
    public static (Message Title, Message Body) Describe(
        IReadOnlyList<QuarantinedStore> quarantined)
    {
        var labels = quarantined.Select(q => q.Label).Distinct().ToArray();
        return labels switch
        {
            [SettingsLabel] => (Message.Of("quarantine.settingsTitle"), Message.Of("quarantine.settingsBody")),
            [PathListLabel] => (Message.Of("quarantine.pathListTitle"), Message.Of("quarantine.pathListBody")),
            _ => (Message.Of("quarantine.manyTitle"), Message.Of("quarantine.manyBody")),
        };
    }
}
