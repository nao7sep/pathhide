using System;
using PathHide.I18n;

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

    public static Message SettingsSave(Exception error) => error is UnauthorizedAccessException
        ? Message.Of("failure.settingsSavePermission")
        : Message.Of("failure.settingsSave");

    public static Message PathListSave(Exception error) => error is UnauthorizedAccessException
        ? Message.Of("failure.pathListSavePermission")
        : Message.Of("failure.pathListSave");

    public static Message Scan(Exception error) => error is UnauthorizedAccessException
        ? Message.Of("failure.scanPermission")
        : Message.Of("failure.scan");

    public static Message PathPicker(Exception error) => Message.Of("failure.pathPicker");

    public static Message WindowAction(Exception error) => Message.Of("failure.windowAction");

    public static Message LogReveal() => Message.Of("failure.logReveal");

    public static Message Startup() => Message.Of("failure.startupData");

    public static Message PathListStartup() => Message.Of("failure.pathListStartup");
}
