using Xunit;

namespace PathHide.Tests.Fakes;

/// <summary>
/// Test collection for the classes that start the process-wide log with a sink of their own. A second
/// start while one is running keeps the first sink, so a test running beside another would never see
/// its records; grouping them disables parallel execution across the classes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessLogCollection
{
    public const string Name = "Process log";
}
