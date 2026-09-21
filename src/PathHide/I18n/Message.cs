using System;
using System.Collections.Generic;
using System.Linq;

namespace PathHide.I18n;

/// <summary>A value filled into a message's <c>{name}</c> placeholder.</summary>
public readonly record struct MessageValue(string Name, object? Value)
{
    public static implicit operator MessageValue((string Name, object? Value) pair) =>
        new(pair.Name, pair.Value);
}

/// <summary>
/// Text as it is held rather than shown: the key and the values it is filled with, rendered by the
/// translator at the moment it reaches the screen (localization conventions).
///
/// A view model, a service or a result keeps one of these, so a sentence follows a language change
/// instead of freezing in the language it was built in, and so no English is assembled anywhere but
/// the catalogue.
/// </summary>
public sealed record Message(string Key, IReadOnlyList<MessageValue> Values)
{
    public static Message Of(string key, params MessageValue[] values) => new(key, values);

    /// <summary>A message whose value is another message, rendered in the same language.</summary>
    public static Message Of(string key, string valueName, Message nested) =>
        new(key, [new MessageValue(valueName, nested)]);

    /// <summary>
    /// Several messages read as one line, joined through <paramref name="joinKey"/>, an entry holding
    /// <c>{first}</c> and <c>{rest}</c> (localization conventions). Each part keeps its own plural
    /// form, and the language decides the separator, because a list glued with a fixed ", " is
    /// English punctuation in every language. One part is returned as it is.
    /// </summary>
    public static Message Join(string joinKey, IReadOnlyList<Message> parts) => parts.Count switch
    {
        0 => throw new ArgumentException("Nothing to join.", nameof(parts)),
        1 => parts[0],
        _ => Of(joinKey, ("first", parts[0]), ("rest", Join(joinKey, parts.Skip(1).ToArray()))),
    };

    /// <summary>
    /// The key, never English. A message is meant to be rendered by the translator; if one ever
    /// reaches a binding or a log line unrendered, its key shows, which the rendered-key gate fails on
    /// rather than letting a fixed language reach the screen.
    /// </summary>
    public override string ToString() => Key;
}
