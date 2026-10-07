using System;
using System.Threading;
using System.Threading.Tasks;
using PathHide.I18n;
using Xunit;

namespace PathHide.Tests.I18n;

public sealed class LanguageBootstrapTests
{
    [Fact]
    public async Task A_language_read_timeout_uses_system_for_the_startup_failure_and_never_admits_the_shell()
    {
        using var release = new ManualResetEventSlim();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var earlier = App.StartupFailureMessage;
        try
        {
            App.StartupFailureMessage = null;
            var preference = LanguageBootstrap.ReadPreferenceForStartup(() =>
            {
                release.Wait();
                returned.TrySetResult();
                return "ja";
            }, TimeSpan.FromMilliseconds(100));
            Assert.Equal(Languages.System, preference);
            Assert.Equal("failure.startupStorage", App.StartupFailureMessage?.Key);
        }
        finally
        {
            release.Set();
            try { await returned.Task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
            finally { App.StartupFailureMessage = earlier; }
        }
    }

    [Fact]
    public void A_timely_language_read_preserves_the_saved_choice()
    {
        var earlier = App.StartupFailureMessage;
        try
        {
            App.StartupFailureMessage = null;
            Assert.Equal("ja", LanguageBootstrap.ReadPreferenceForStartup(() => "ja", TimeSpan.FromSeconds(2)));
            Assert.Null(App.StartupFailureMessage);
        }
        finally { App.StartupFailureMessage = earlier; }
    }
}
