using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using PathHide.I18n;
using Xunit;

namespace PathHide.Tests.I18n;

/// <summary>
/// The computer's own languages as macOS answers them, checked against what the system's
/// <c>defaults</c> tool reads from the same preference, so the Objective-C path is proven on a real Mac.
/// </summary>
public sealed class ComputerLanguagesTests
{
    [MacOnlyFact]
    public void On_macOS_the_list_is_the_computer_s_own_preferred_languages_in_order()
    {
        var expected = SystemPreferredLanguages();

        var read = ComputerLanguages.Read();

        Assert.Equal(expected, read.Take(expected.Length));
    }

    // `defaults read -g AppleLanguages` prints a property-list array of quoted or bare tags.
    private static string[] SystemPreferredLanguages()
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/defaults", "read -g AppleLanguages")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)), "defaults did not answer.");
        Assert.Equal(0, process.ExitCode);

        var languages = Regex.Matches(output, @"^\s*""?([A-Za-z0-9-]+)""?,?\s*$", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.NotEmpty(languages);
        return languages;
    }
}
