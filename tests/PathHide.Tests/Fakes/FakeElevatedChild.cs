using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Services;

namespace PathHide.Tests.Fakes;

/// <summary>
/// An <see cref="ElevatedChildLauncher"/> that stands in for the UAC-elevated child. It reads its
/// command line and request file the way the real child does (so every launch also exercises the
/// child-side parse), and the test decides when it reports each path and when it exits.
/// </summary>
public sealed class FakeElevatedChild
{
    private TaskCompletionSource<Run> _nextLaunch = NewLaunchSignal();

    public List<Run> Runs { get; } = [];

    /// <summary>When true, the consent prompt is declined and no child runs.</summary>
    public bool Decline { get; set; }

    /// <summary>When set, the launch waits for it, the way the consent prompt holds a launch.</summary>
    public TaskCompletionSource? Prompt { get; set; }

    /// <summary>Completes once a launch is waiting on <see cref="Prompt"/>.</summary>
    public TaskCompletionSource Prompting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Runs inside the launch, once the child has started — e.g. to report and exit at once.</summary>
    public Action<Run>? OnStart { get; set; }

    /// <summary>Completes with the next child that starts. Read it before triggering the launch.</summary>
    public Task<Run> NextLaunch => _nextLaunch.Task;

    public int LaunchCount
    {
        get
        {
            lock (Runs)
                return Runs.Count;
        }
    }

    public async Task<Task<int>?> LaunchAsync(IReadOnlyList<string> arguments)
    {
        var invocation = ElevatedApplyCommand.ParseArguments(arguments)
            ?? throw new InvalidOperationException("The child could not parse its command line.");
        var request = ElevatedApplyCommand.ParseRequest(await File.ReadAllTextAsync(invocation.RequestPath));

        if (Prompt is { } prompt)
        {
            Prompting.TrySetResult();
            await prompt.Task;
        }
        if (Decline)
            return null;

        var run = new Run(arguments, invocation, request);
        lock (Runs)
            Runs.Add(run);
        OnStart?.Invoke(run);
        Interlocked.Exchange(ref _nextLaunch, NewLaunchSignal()).SetResult(run);
        return run.Exited;
    }

    private static TaskCompletionSource<Run> NewLaunchSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed class Run(
        IReadOnlyList<string> arguments,
        ElevatedApplyCommand.Invocation invocation,
        ElevatedApplyCommand.Buckets request)
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Arguments { get; } = arguments;
        public ElevatedApplyCommand.Invocation Invocation { get; } = invocation;
        public ElevatedApplyCommand.Buckets Request { get; } = request;
        public Task<int> Exited => _exit.Task;

        /// <summary>Appends one result line, as the child does once a path is done.</summary>
        public void Report(string path, bool ok = true) =>
            File.AppendAllText(Invocation.ResultsPath!, ElevatedApplyResults.SerializeLine(new PathApplyResult(path, ok)));

        public void Exit(int exitCode = 0) => _exit.TrySetResult(exitCode);
    }
}
