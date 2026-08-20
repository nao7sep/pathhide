using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PathHide;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(PathHide.Tests.TestAppBuilder))]

// Avalonia headless drives every [AvaloniaFact] through one shared application and dispatcher.
// Serialize the assembly so separate test classes cannot claim that dispatcher from different threads.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PathHide.Tests;

/// <summary>
/// Headless Avalonia entry point for the [AvaloniaFact] tests. It reuses the real <see cref="App"/>
/// so its resources load, but the headless lifetime is not a classic desktop one, so the app's own
/// startup (which would create the main window and touch the real storage root) never runs.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
