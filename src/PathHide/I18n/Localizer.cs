using System;
using System.Collections.Generic;
using System.Globalization;

namespace PathHide.I18n;

/// <summary>
/// The language the app is speaking now, and the one translator everything reads through.
///
/// The language is chosen once before the first window is drawn (<see cref="LanguageBootstrap"/>) and
/// again whenever Settings is saved. A change raises <see cref="Changed"/>, which every surface that
/// holds words already on screen listens to: the attached properties in <see cref="Localized"/>, the
/// view models, and the macOS menu bar. Nothing restarts.
/// </summary>
internal static class Localizer
{
    private static Translator s_translator =
        new(Languages.English, CultureInfo.GetCultureInfo(Languages.English));

    /// <summary>The translator for the current language. Never null, from the first line of the app.</summary>
    internal static Translator Current => s_translator;

    /// <summary>The current language's tag.</summary>
    internal static string Language => s_translator.Tag;

    /// <summary>The preference as saved: a tag, or <see cref="Languages.System"/>.</summary>
    internal static string Preference { get; private set; } = Languages.System;

    /// <summary>Raised after the language has changed, on the thread that changed it.</summary>
    internal static event Action? Changed;

    /// <summary>
    /// Speaks <paramref name="preference"/> from now on. <paramref name="computerLanguages"/> is the
    /// computer's own list, in order, which decides what System means; it is read once at launch and
    /// passed back in on a later change so the answer cannot drift mid-session.
    /// </summary>
    internal static void Use(string? preference, IReadOnlyList<string> computerLanguages)
    {
        var normalized = Languages.NormalizePreference(preference);
        var tag = Languages.Resolve(normalized, computerLanguages);
        Preference = normalized;
        if (tag == s_translator.Tag)
            return;

        s_translator = new Translator(tag, Languages.FormattingCulture(tag, CultureInfo.CurrentCulture));
        Changed?.Invoke();
    }

    /// <summary>The words for a key, as a shorthand for <c>Localizer.Current.T</c>.</summary>
    internal static string T(string key, params MessageValue[] values) => s_translator.T(key, values);

    /// <summary>The words for a held message.</summary>
    internal static string Of(Message message) => s_translator.Of(message)!;

    /// <summary>
    /// Speaks a language for the duration of one test, and restores the previous one. Only tests use
    /// this; the app changes its language through <see cref="Use"/>.
    /// </summary>
    internal static IDisposable Speaking(string tag)
    {
        var previous = s_translator;
        var previousPreference = Preference;
        void Undo()
        {
            s_translator = previous;
            Preference = previousPreference;
        }

        s_translator = new Translator(tag, Languages.FormattingCulture(tag, CultureInfo.CurrentCulture));
        Preference = tag;
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // A handler that throws fails the test that switched, and must not leave every test after
            // it speaking this language as well.
            Undo();
            throw;
        }

        return new Restore(() =>
        {
            Undo();
            Changed?.Invoke();
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
