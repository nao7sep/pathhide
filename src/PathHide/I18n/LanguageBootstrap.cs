using System;
using System.IO;
using System.Text.Json;
using PathHide.Models;
using PathHide.Storage;
using static PathHide.Views.ObjC;

namespace PathHide.I18n;

/// <summary>
/// Settles the interface language before anything draws.
///
/// Two things have to happen this early. The first window's very first frame must already be in the
/// reader's language, and on macOS the items AppKit contributes itself — Services, Emoji &amp;
/// Symbols, Start Dictation — are drawn in the language it settles on when the application object is
/// created, which Avalonia does as it starts. So the language is resolved and handed to AppKit before
/// the app is built, from the saved preference read straight out of <c>config.json</c>.
///
/// The read is deliberately its own, and forgiving: the real store quarantines a file it cannot
/// parse, and that decision belongs to the app's startup path, not to a language lookup. A file that
/// cannot be read here simply means System, and the startup-failure surfaces then speak the
/// computer's language, which is what the localization conventions ask of them.
/// </summary>
internal static class LanguageBootstrap
{
    /// <summary>Resolves the language, tells the app, and points AppKit at it. Returns the computer's
    /// own languages, which the app keeps so a later change resolves System the same way.</summary>
    internal static System.Collections.Generic.IReadOnlyList<string> Start()
    {
        var computerLanguages = ComputerLanguages.Read();
        Localizer.Use(SavedPreference(), computerLanguages);
        AlignAppKit(Localizer.Language);
        return computerLanguages;
    }

    /// <summary>The language preference in <c>config.json</c>, or System when there is none to read.</summary>
    internal static string SavedPreference()
    {
        try
        {
            var path = Path.Combine(StorageRoot.Directory, AppSettings.FileName);
            if (!File.Exists(path))
                return Languages.System;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("language", out var language)
                && language.ValueKind == JsonValueKind.String
                ? Languages.NormalizePreference(language.GetString())
                : Languages.System;
        }
        catch (Exception)
        {
            return Languages.System;
        }
    }

    /// <summary>
    /// Points AppKit at <paramref name="tag"/> by putting it in the argument domain of the user's
    /// defaults, which is volatile: it lives for this process only and writes nothing to the reader's
    /// settings. A language saved mid-session reaches AppKit's own items at the next launch, as the
    /// localization conventions say.
    /// </summary>
    internal static void AlignAppKit(string tag)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            if (Class("NSUserDefaults") == IntPtr.Zero)
                return;

            var languages = Send(Class("NSArray"), "arrayWithObject:", NSString(tag));
            var domain = Send(Class("NSDictionary"), "dictionaryWithObject:forKey:",
                languages, NSString("AppleLanguages"));
            var defaults = Send(Class("NSUserDefaults"), "standardUserDefaults");
            Send(defaults, "setVolatileDomain:forName:", domain, NSString("NSArgumentDomain"));
        }
        catch (Exception)
        {
            // Without this the menu's own items follow the computer instead of the app: a mixed menu,
            // not a broken one, and never a reason to fail to start.
        }
    }
}
