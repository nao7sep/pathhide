using System;
using System.Threading;
using System.Threading.Tasks;
using PathHide.I18n;
using Xunit;
using static PathHide.Views.ObjC;

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

    [MacOnlyFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void On_macOS_AppKit_is_pointed_at_the_language_through_the_volatile_argument_domain()
    {
        var defaults = Send(Class("NSUserDefaults"), "standardUserDefaults");
        var argumentDomain = NSString("NSArgumentDomain");
        var previous = Send(defaults, "volatileDomainForName:", argumentDomain);
        var savedBefore = SavedAppleLanguages(defaults);
        try
        {
            LanguageBootstrap.AlignAppKit("ko");

            var languages = Send(defaults, "objectForKey:", NSString("AppleLanguages"));
            Assert.Equal(1UL, SendForUInt(languages, "count"));
            Assert.Equal("ko", String(SendWithIndex(languages, "objectAtIndex:", 0)));
            // Set for this process only: the reader's saved preference is untouched.
            Assert.Equal(savedBefore, SavedAppleLanguages(defaults));
        }
        finally
        {
            // The argument domain is process-wide; give the rest of the run the one it started with.
            if (previous == IntPtr.Zero)
                Send(defaults, "removeVolatileDomainForName:", argumentDomain);
            else
                Send(defaults, "setVolatileDomain:forName:", previous, argumentDomain);
        }
    }

    /// <summary>The AppleLanguages the reader saved in the global domain, as text, or null when none.</summary>
    private static string? SavedAppleLanguages(IntPtr defaults)
    {
        var global = Send(defaults, "persistentDomainForName:", NSString("NSGlobalDomain"));
        var saved = global == IntPtr.Zero ? IntPtr.Zero : Send(global, "objectForKey:", NSString("AppleLanguages"));
        return saved == IntPtr.Zero ? null : String(Send(saved, "description"));
    }
}
