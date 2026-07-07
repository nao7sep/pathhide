using System;
using System.ComponentModel;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

public sealed class ExternalLauncherTests
{
    [Fact]
    public void Open_reports_the_failure_and_does_not_throw_when_the_launch_fails()
    {
        const string url = "https://example.test/no-handler";
        // The Win32Exception thrown when no handler is registered is the exact failure
        // that would otherwise bubble out of a UI click handler as an unhandled crash.
        var failure = new Win32Exception("no application is associated");
        string? reportedUrl = null;
        Exception? reportedError = null;

        // A bare throw from here would fail the test, proving the boundary catch holds;
        // the failure must instead reach the error sink (in production, Log.Error).
        ExternalLauncher.Open(
            url,
            start: _ => throw failure,
            onError: (u, ex) => { reportedUrl = u; reportedError = ex; });

        Assert.Equal(url, reportedUrl);
        Assert.Same(failure, reportedError);
    }

    [Fact]
    public void Open_passes_the_target_to_the_launcher_on_success()
    {
        const string url = "https://example.test/ok";
        string? launched = null;
        var reported = false;

        ExternalLauncher.Open(
            url,
            start: u => launched = u,
            onError: (_, _) => reported = true);

        Assert.Equal(url, launched);
        Assert.False(reported);
    }
}
