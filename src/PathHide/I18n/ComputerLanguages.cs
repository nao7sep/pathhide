using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using static PathHide.Views.ObjC;

namespace PathHide.I18n;

/// <summary>
/// The languages the computer is set to, most preferred first.
///
/// Read once, at launch, so System cannot mean one language in the menu bar and another in a window
/// opened an hour later. Both platforms keep an ordered list rather than a single language, and the
/// order is what decides which of the interface languages a reader gets.
/// </summary>
internal static class ComputerLanguages
{
    /// <summary>
    /// The list, most preferred first. Never empty: a platform that answers nothing falls back to the
    /// culture .NET resolved for the interface.
    /// </summary>
    internal static IReadOnlyList<string> Read()
    {
        try
        {
            var preferred = OperatingSystem.IsMacOS() ? MacPreferred()
                : OperatingSystem.IsWindows() ? WindowsPreferred()
                : [];
            if (preferred.Count > 0)
                return preferred;
        }
        catch (Exception)
        {
            // A platform that will not answer is not a reason to fail to start; the fallback below is
            // still a real answer about this computer.
        }

        return [CultureInfo.CurrentUICulture.Name, CultureInfo.CurrentCulture.Name];
    }

    private static IReadOnlyList<string> MacPreferred()
    {
        var languages = Send(Class("NSLocale"), "preferredLanguages");
        if (languages == IntPtr.Zero)
            return [];

        var count = SendForUInt(languages, "count");
        var preferred = new List<string>((int)count);
        for (ulong index = 0; index < count; index++)
            preferred.Add(String(SendWithIndex(languages, "objectAtIndex:", index)));
        return preferred;
    }

    private static IReadOnlyList<string> WindowsPreferred()
    {
        const uint byName = 0x8; // MUI_LANGUAGE_NAME

        uint languages = 0;
        uint length = 0;
        if (!GetUserPreferredUILanguages(byName, out languages, null, ref length) || length == 0)
            return [];

        var buffer = new char[length];
        if (!GetUserPreferredUILanguages(byName, out languages, buffer, ref length))
            return [];

        // A double-null-terminated block of names.
        var preferred = new List<string>((int)languages);
        var name = new StringBuilder();
        foreach (var character in buffer)
        {
            if (character != '\0')
            {
                name.Append(character);
                continue;
            }

            if (name.Length == 0)
                break;
            preferred.Add(name.ToString());
            name.Clear();
        }

        return preferred;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(
        uint flags, out uint languages, char[]? buffer, ref uint length);
}
