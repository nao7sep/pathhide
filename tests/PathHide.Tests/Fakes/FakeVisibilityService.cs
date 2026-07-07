using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Models;
using PathHide.Services;

namespace PathHide.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IVisibilityService"/> for tests. Inspections are looked
/// up by path (with a configurable default); Hide/Show flip the recorded state
/// so an inspect-after-apply reflects the change. All calls are recorded.
/// </summary>
public sealed class FakeVisibilityService : IVisibilityService
{
    private readonly Dictionary<string, PathInspection> _byPath = new(StringComparer.Ordinal);

    public PathInspection Default { get; set; } = new(ActualState.Visible, ItemKind.File);

    public ConcurrentQueue<string> Inspected { get; } = new();
    public ConcurrentQueue<string> Hidden { get; } = new();
    public ConcurrentQueue<string> Shown { get; } = new();

    /// <summary>When set, the next matching call throws this exception once.</summary>
    public Func<string, Exception?>? OnInspect { get; set; }

    /// <summary>When set and it returns non-null, <see cref="Hide"/> throws instead of recording.</summary>
    public Func<string, Exception?>? OnHide { get; set; }

    /// <summary>
    /// When set, <see cref="Inspect"/> blocks on this gate before returning, letting a test hold a
    /// scan or apply pass mid-flight (the call runs on a thread-pool thread via the scanner/apply's
    /// <c>Task.Run</c>, so blocking it does not stall the test thread).
    /// </summary>
    public ManualResetEventSlim? InspectGate { get; set; }

    /// <summary>
    /// Completes the first time <see cref="Inspect"/> is entered — just before it parks on
    /// <see cref="InspectGate"/>. Lets a test wait until an inspection is genuinely in-flight before
    /// acting, closing the race where the scanner's <c>Task.Run</c> has not yet been picked up by a
    /// thread-pool thread (cancelling before then would skip the delegate and never inspect the path).
    /// </summary>
    public TaskCompletionSource InspectEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Set(string path, ActualState state, ItemKind kind = ItemKind.File)
        => _byPath[path] = new PathInspection(state, kind);

    public PathInspection Inspect(string path)
    {
        Inspected.Enqueue(path);

        var thrown = OnInspect?.Invoke(path);
        if (thrown is not null)
            throw thrown;

        InspectEntered.TrySetResult();
        InspectGate?.Wait();

        return _byPath.TryGetValue(path, out var inspection) ? inspection : Default;
    }

    public void Hide(string path)
    {
        var thrown = OnHide?.Invoke(path);
        if (thrown is not null)
            throw thrown;

        Hidden.Enqueue(path);
        _byPath[path] = new PathInspection(
            ActualState.Hidden,
            _byPath.TryGetValue(path, out var existing) ? existing.ItemKind : Default.ItemKind);
    }

    public void Show(string path)
    {
        Shown.Enqueue(path);
        _byPath[path] = new PathInspection(
            ActualState.Visible,
            _byPath.TryGetValue(path, out var existing) ? existing.ItemKind : Default.ItemKind);
    }
}
