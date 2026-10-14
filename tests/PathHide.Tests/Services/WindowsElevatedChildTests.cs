using System;
using System.ComponentModel;
using System.IO;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

/// <summary>
/// The UAC launch's own decisions, which run anywhere: the start it asks the shell for and the failure
/// that means the user declined. The consent prompt itself is native and not raised by any test.
/// </summary>
public sealed class WindowsElevatedChildTests
{
    [Fact]
    public void The_child_starts_through_the_runas_verb_with_every_argument_whole()
    {
        var arguments = ElevatedApplyCommand.BuildArguments(
            @"C:\Users\山田 太郎\AppData\Local\Temp\pathhide-apply-PC.1.a.request.json",
            @"C:\Users\山田 太郎\AppData\Local\Temp\pathhide-apply-PC.1.a.results.jsonl",
            @"D:\Data Root\",
            new ElevatedApplyCommand.Parent(1234, 638_000_000_000_000_000));

        var start = WindowsElevatedChild.CreateStartInfo(@"C:\Program Files\PathHide\PathHide.exe", arguments);

        Assert.Equal("runas", start.Verb);
        Assert.True(start.UseShellExecute);
        Assert.Equal(@"C:\Program Files\PathHide\PathHide.exe", start.FileName);
        Assert.Equal(arguments, start.ArgumentList);
        Assert.Equal(
            ElevatedApplyCommand.ParseArguments(arguments),
            ElevatedApplyCommand.ParseArguments([.. start.ArgumentList]));
    }

    [Fact]
    public void Only_ERROR_CANCELLED_means_the_user_declined_the_prompt()
    {
        Assert.True(WindowsElevatedChild.IsDeclined(new Win32Exception(1223)));
        Assert.False(WindowsElevatedChild.IsDeclined(new Win32Exception(5)));
        Assert.False(WindowsElevatedChild.IsDeclined(new IOException("1223")));
    }
}
