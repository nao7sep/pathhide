using System;
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

    /// <summary>The startup halt for a path list that could not be opened, or whose content is not a path list.</summary>
    public static Message PathListStartup(UnreadableStoreException unreadable) =>
        Message.Of(unreadable.IsAccessFailure ? "failure.pathListStartupAccess" : "failure.pathListStartup",
            ("path", unreadable.Path));
}
