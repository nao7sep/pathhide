using System;
using System.Collections.Generic;
using System.Linq;
using PathHide.I18n;
using PathHide.Storage;

namespace PathHide.ViewModels;

/// <summary>
/// Owns the user-safe presentation of failures whose diagnostics remain in the log.
///
/// Each method answers with the key of a sentence, never the sentence: what the reader sees is
/// chosen here, and which language they see it in is decided where it is shown.
/// </summary>
public static class FailurePresentation
{
    public static Message StartupStorage() => Message.Of("failure.startupStorage");

    public static Message SettingsSave(Exception error) => error switch
    {
        NewerFormatException newer => NewerStore(newer),
        UnreadableStoreException unreadable => Message.Of("failure.settingsUnreadable", ("path", unreadable.Path)),
        UnauthorizedAccessException => Message.Of("failure.settingsSavePermission"),
        TimeoutException => Message.Of("failure.settingsSaveTimeout"),
        _ => Message.Of("failure.settingsSave"),
    };

    public static Message PathListSave(Exception error) => error switch
    {
        NewerFormatException newer => NewerStore(newer),
        UnreadableStoreException unreadable => PathListUnreadable(unreadable),
        UnauthorizedAccessException => Message.Of("failure.pathListSavePermission"),
        TimeoutException => Message.Of("failure.pathListSaveTimeout"),
        _ => Message.Of("failure.pathListSave"),
    };

    /// <summary>
    /// A path list PathHide cannot read, named by its path and left as it is: either the file could not
    /// be opened, or its content is not a path list.
    /// </summary>
    public static Message PathListUnreadable(UnreadableStoreException unreadable) =>
        Message.Of(unreadable.IsAccessFailure ? "failure.pathListUnreadableAccess" : "failure.pathListUnreadable",
            ("path", unreadable.Path));

    /// <summary>A file a newer PathHide wrote, named by its path and left as it is.</summary>
    public static Message NewerStore(NewerFormatException newer) =>
        Message.Of("failure.newerStore", ("path", newer.Path));

    public static Message Scan(Exception error) => error is UnauthorizedAccessException
        ? Message.Of("failure.scanPermission")
        : Message.Of("failure.scan");

    public static Message PathPicker(Exception error) => Message.Of("failure.pathPicker");

    public static Message WindowAction(Exception error) => Message.Of("failure.windowAction");

    /// <summary>An unexpected startup failure, naming where its details were logged.</summary>
    public static Message Startup() =>
        Message.Of("failure.startupUnexpected", ("records", StorageRoot.RecordsFile), ("logs", StorageRoot.LogsDirectory));

    /// <summary>
    /// The launch stop: every refused file, each named with why, then what the user can do. Nothing was
    /// written, and every file is left as it is.
    /// </summary>
    public static Message StartupRefused(IReadOnlyList<StartupRefusal> refusals) =>
        Message.Of("startup.refused", "files", Message.Join("startup.refusedJoin", refusals
            .Select(refusal => Message.Of(refusal.Reason switch
            {
                RefusalReason.Newer => "startup.refusedNewer",
                RefusalReason.Inaccessible => "startup.refusedInaccessible",
                _ => "startup.refusedUnreadable",
            }, ("path", refusal.Path)))
            .ToArray()));

    /// <summary>The records database could not be read at launch: left as it is, and off for the session.</summary>
    public static Message RecordsOff() =>
        Message.Of("failure.recordsOff", ("path", StorageRoot.RecordsFile), ("logs", StorageRoot.LogsDirectory));

    /// <summary>The backup history could not be read at launch: left as it is, and off for the session.</summary>
    public static Message BackupsOff() =>
        Message.Of("failure.backupsOff", ("path", System.IO.Path.Combine(StorageRoot.Directory, Backup.BackupStore.FileName)));
}
