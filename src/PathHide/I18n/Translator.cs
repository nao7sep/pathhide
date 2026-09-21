using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PathHide.I18n;

/// <summary>
/// Turns a key and its values into the words on screen, in one language.
///
/// A translator is immutable and cheap: the app keeps one for the current language and builds a new
/// one when the language changes, so nothing has to be told that the words underneath it moved.
/// </summary>
internal sealed class Translator
{
    private readonly Catalogue _catalogue;
    private readonly Catalogue _english;

    internal string Tag { get; }

    /// <summary>The culture dates and numbers are formatted in, which is not always the language's own.</summary>
    internal CultureInfo Culture { get; }

    internal Translator(string tag, CultureInfo culture)
    {
        Tag = tag;
        Culture = culture;
        _catalogue = Catalogue.For(tag);
        _english = tag == Languages.English ? _catalogue : Catalogue.For(Languages.English);
    }

    /// <summary>The words for <paramref name="key"/>, with its values filled in.</summary>
    internal string T(string key, params MessageValue[] values) => Render(key, values);

    /// <summary>The words for a held message, or nothing when there is none.</summary>
    internal string? Of(Message? message) => message is null ? null : Render(message.Key, message.Values);

    private string Render(string key, IReadOnlyList<MessageValue> values)
    {
        var template = Template(key, values);
        return template is null ? key : Fill(template, values);
    }

    private string? Template(string key, IReadOnlyList<MessageValue> values)
    {
        // A key the language is missing falls back to English rather than to nothing, so a catalogue
        // that slips through the gate costs the reader one English sentence, not a blank interface.
        var language = Tag;
        if (!_catalogue.TryGet(key, out var entry))
        {
            if (!_english.TryGet(key, out entry))
                return null;
            language = Languages.English;
        }

        if (!entry.IsPlural)
            return entry.Text;

        // The form follows the language the words came from, so a fallback sentence still agrees with
        // its own number.
        var count = CountIn(values);
        var category = count is null ? Plural.Other : Plural.CategoryFor(language, count.Value);
        var forms = entry.Forms!;
        return forms.TryGetValue(category, out var form) ? form
            : forms.TryGetValue(Plural.Other, out var other) ? other
            : null;
    }

    private static long? CountIn(IReadOnlyList<MessageValue> values)
    {
        foreach (var value in values)
        {
            if (value.Name == "count" && value.Value is not null)
                return Convert.ToInt64(value.Value, CultureInfo.InvariantCulture);
        }

        return null;
    }

    private string Fill(string template, IReadOnlyList<MessageValue> values)
    {
        if (values.Count == 0 || template.IndexOf('{') < 0)
            return template;

        var text = new StringBuilder(template.Length + 16);
        for (var index = 0; index < template.Length; index++)
        {
            var character = template[index];
            if (character != '{')
            {
                text.Append(character);
                continue;
            }

            var close = template.IndexOf('}', index + 1);
            if (close < 0)
            {
                text.Append(template, index, template.Length - index);
                break;
            }

            var name = template[(index + 1)..close];
            text.Append(Value(name, values) ?? template[index..(close + 1)]);
            index = close;
        }

        return text.ToString();
    }

    private string? Value(string name, IReadOnlyList<MessageValue> values)
    {
        foreach (var value in values)
        {
            if (value.Name != name)
                continue;

            return value.Value switch
            {
                null => "",
                Message nested => Of(nested),
                // A number filled into a sentence is formatted for the reader's locale, so a thousands
                // separator and a decimal mark look the way they do everywhere else on their computer.
                int number => Number(number),
                long number => Number(number),
                double number => number.ToString("0.##", Culture),
                IFormattable formattable => formattable.ToString(null, Culture),
                var other => other.ToString() ?? "",
            };
        }

        return null;
    }

    /// <summary>A whole number, grouped for the reader's locale.</summary>
    internal string Number(long value) => value.ToString("N0", Culture);
}
