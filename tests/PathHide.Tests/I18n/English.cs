using System.Globalization;
using PathHide.I18n;

namespace PathHide.Tests.I18n;

/// <summary>
/// Reads a held message back in English, so a test can still say what the reader is told.
///
/// A test asserts the English because English is the source language: it is the one wording the app
/// itself decides. What the other nine say is the catalogue gate's business, not each test's.
/// </summary>
internal static class English
{
    private static readonly Translator Translator = new("en", CultureInfo.GetCultureInfo("en"));

    internal static string Of(Message? message) => Translator.Of(message) ?? string.Empty;

    internal static string Of(string key, params MessageValue[] values) => Translator.T(key, values);
}
