using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PathHide.I18n;
using Xunit;

namespace PathHide.Tests.I18n;

/// <summary>
/// The computer's own languages: on macOS checked against what the system's <c>defaults</c> tool reads
/// from the same preference, so the Objective-C path is proven on a real Mac; on Windows the native list
/// read directly, and its block format parsed anywhere.
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

    [WindowsOnlyFact]
    public void On_Windows_the_native_list_names_real_cultures()
    {
        var read = ComputerLanguages.WindowsPreferred();

        Assert.NotEmpty(read);
        Assert.All(read, name => Assert.Equal(name, CultureInfo.GetCultureInfo(name).Name, ignoreCase: true));
    }

    [Theory]
    // Each | stands for a null character.
    [InlineData("ja-JP|en-US||", new[] { "ja-JP", "en-US" })]
    [InlineData("en-US||", new[] { "en-US" })]
    // What follows the terminating empty name is not part of the list.
    [InlineData("de-DE||fr-FR||", new[] { "de-DE" })]
    // A block cut short still yields its whole names, never a fragment run on past the buffer.
    [InlineData("zh-Hans-CN|ko", new[] { "zh-Hans-CN" })]
    [InlineData("||", new string[0])]
    [InlineData("", new string[0])]
    public void The_Windows_name_block_reads_up_to_its_empty_name(string block, string[] expected) =>
        Assert.Equal(expected, ComputerLanguages.ParseNameBlock(block.Replace('|', '\0')));

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
