using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PathHide;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(PathHide.Tests.TestAppBuilder))]

// Avalonia headless drives every [AvaloniaFact] through one shared application and dispatcher.
// Serialize the assembly so separate test classes cannot claim that dispatcher from different threads.
//
// xunit.v3 4.0 marks this obsolete-as-error and points at ParallelizationAttribute, which the
// release does not ship — nor is the IParallelizationAttribute extensibility point it was to
// implement reachable from this project's references. So the setting is kept and the one call
// site suppressed, rather than the dependency being held back or the tests left to run in
// parallel against a single shared dispatcher. Remove the pragma once the replacement lands.
#pragma warning disable CS0619
[assembly: CollectionBehavior(DisableTestParallelization = true)]
#pragma warning restore CS0619

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
