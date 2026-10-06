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
        NewerFormatException newer => NewerStore(newer),
        UnauthorizedAccessException => Message.Of("failure.settingsSavePermission"),
        _ => Message.Of("failure.settingsSave"),
    };

    public static Message PathListSave(Exception error) => error switch
    {
        NewerFormatException newer => NewerStore(newer),
        UnreadableStoreException unreadable => PathListUnreadable(unreadable),
        UnauthorizedAccessException => Message.Of("failure.pathListSavePermission"),
        _ => Message.Of("failure.pathListSave"),
    };

    /// <summary>A path list PathHide cannot read, named by its path and left as it is.</summary>
    public static Message PathListUnreadable(UnreadableStoreException unreadable) =>
        Message.Of("failure.pathListUnreadable", ("path", unreadable.Path));

    /// <summary>A file a newer PathHide wrote, named by its path and left as it is.</summary>
    public static Message NewerStore(NewerFormatException newer) =>
        Message.Of("failure.newerStore", ("path", newer.Path));

    public static Message Scan(Exception error) => error is UnauthorizedAccessException
        ? Message.Of("failure.scanPermission")
        : Message.Of("failure.scan");

    public static Message PathPicker(Exception error) => Message.Of("failure.pathPicker");

    public static Message WindowAction(Exception error) => Message.Of("failure.windowAction");

    public static Message Startup() => Message.Of("failure.startupUnexpected");

    /// <summary>A store that could not be read or set aside, named by its path and left as it is.</summary>
    public static Message StartupUnreadable(UnreadableStoreException unreadable) =>
        Message.Of("failure.startupData", ("path", unreadable.Path));

    public static Message PathListStartup(UnreadableStoreException unreadable) =>
        Message.Of("failure.pathListStartup", ("path", unreadable.Path));
}
