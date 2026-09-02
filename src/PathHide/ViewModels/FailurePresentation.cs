using System;

namespace PathHide.ViewModels;

/// <summary>Owns the user-safe presentation of failures whose diagnostics remain in the log.</summary>
public static class FailurePresentation
{
    public static string SettingsSave(Exception error) => error is UnauthorizedAccessException
        ? "Settings could not be saved. Check that the PathHide data folder is writable, then try again."
        : "Settings could not be saved. Nothing was changed; try again.";

    public static string PathListSave(Exception error) => error is UnauthorizedAccessException
        ? "The path list could not be saved. Check that the PathHide data folder is writable, then try again."
        : "The path list could not be saved. Your existing list is unchanged; try again.";

    public static string Scan(Exception error) => error is UnauthorizedAccessException
        ? "Some paths could not be scanned because PathHide did not have permission to inspect them."
        : "The path scan could not be completed. Your existing results are still shown; try Reload again.";

    public static string Startup() =>
        "A settings file could not be read, and PathHide could not set it aside either, so it was left " +
        "unchanged rather than risk overwriting it. Your files were not hidden or unhidden. Repair or " +
        "move the affected file under the PathHide data folder, then start PathHide again.";

    public static string PathListStartup() =>
        "Your list of tracked paths could not be read, so PathHide preserved it rather than replacing " +
        "it or starting with an empty list. Your files were not hidden or unhidden. Check the session " +
        "log for the preserved copy, repair it or move it out of the way to start fresh, then start " +
        "PathHide again.";
}
