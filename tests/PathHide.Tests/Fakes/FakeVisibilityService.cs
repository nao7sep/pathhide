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
    private readonly ConcurrentDictionary<string, PathInspection> _byPath = new(StringComparer.Ordinal);

    public PathInspection Default { get; set; } = new(ActualState.Visible, ItemKind.File);

    public bool WritesTakeEffect { get; set; } = true;

    public ConcurrentQueue<string> Inspected { get; } = new();
    public ConcurrentQueue<string> Hidden { get; } = new();
    /// <summary>What each Hide and Show was asked to do to System, in call order per kind.</summary>
    public ConcurrentQueue<SystemBit> HideSystem { get; } = new();
    public ConcurrentQueue<SystemBit> ShowSystem { get; } = new();

    /// <summary>The paths whose System attribute is on, as the writes leave it.</summary>
    public ConcurrentDictionary<string, bool> SystemOn { get; } = new(StringComparer.Ordinal);
    public ConcurrentQueue<string> Shown { get; } = new();

    /// <summary>When set, the next matching call throws this exception once.</summary>
    public Func<string, Exception?>? OnInspect { get; set; }


    /// <summary>When set and it returns non-null, <see cref="Hide"/> throws instead of recording.</summary>
    public Func<string, Exception?>? OnHide { get; set; }

    /// <summary>
    /// When set, <see cref="Inspect"/> blocks on this gate before returning, letting a test hold a
    /// scan or apply pass mid-flight (VisibilityCalls runs the call on a thread of its own, so
    /// blocking it does not stall the test thread).
    /// </summary>
    public ManualResetEventSlim? InspectGate { get; set; }

    /// <summary>
    /// Completes the first time <see cref="Inspect"/> is entered — just before it parks on
    /// <see cref="InspectGate"/>. Lets a test wait until an inspection is genuinely in-flight before
    /// acting, closing the race where the call's thread has not started yet (cancelling before then
    /// would skip the delegate and never inspect the path).
    /// </summary>
    public TaskCompletionSource InspectEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// When set, <see cref="Hide"/> and <see cref="Show"/> block on this gate before writing, the
    /// way a flag write on a stalled network share does. <see cref="WriteEntered"/> completes first.
    /// </summary>
    public ManualResetEventSlim? WriteGate { get; set; }

    /// <summary>Completes the first time <see cref="Hide"/> or <see cref="Show"/> is entered.</summary>
    public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Released once each time <see cref="Hide"/> or <see cref="Show"/> is entered.</summary>
    public SemaphoreSlim WriteEntries { get; } = new(0);

    /// <summary>Path resolutions <see cref="ResolvePath"/> answers with; any other resolves to null.</summary>
    public Dictionary<string, string> Aliases { get; } = new(StringComparer.Ordinal);

    /// <summary>Every path <see cref="ResolvePath"/> was asked about.</summary>
    public ConcurrentQueue<string> Resolved { get; } = new();

    /// <summary>When set, <see cref="ResolvePath"/> blocks on this gate, like a realpath on a dead share.</summary>
    public ManualResetEventSlim? ResolveGate { get; set; }
    public TaskCompletionSource ResolveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Released once each time <see cref="ResolvePath"/> is entered.</summary>
    public SemaphoreSlim ResolveEntries { get; } = new(0);

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

    public bool ResolvesPaths { get; set; } = true;

    public string? ResolvePath(string path)
    {
        Resolved.Enqueue(path);
        ResolveEntered.TrySetResult();
        ResolveEntries.Release();
        ResolveGate?.Wait();
        if (Aliases.TryGetValue(path, out var resolved))
            return resolved;
        var slash = path.LastIndexOf('/');
        return slash > 0 && Aliases.TryGetValue(path[..slash], out resolved)
            ? resolved.TrimEnd('/') + path[slash..] : null;
    }

    public bool Hide(string path, SystemBit system)
    {
        WriteEntered.TrySetResult();
        WriteEntries.Release();
        WriteGate?.Wait();

        var thrown = OnHide?.Invoke(path);
        if (thrown is not null)
            throw thrown;

        Hidden.Enqueue(path);
        HideSystem.Enqueue(system);
        if (!WritesTakeEffect) return false;
        var setNow = WriteSystem(path, system);
        _byPath[path] = new PathInspection(
            ActualState.Hidden,
            _byPath.TryGetValue(path, out var existing) ? existing.ItemKind : Default.ItemKind);
        return setNow;
    }

    public void Show(string path, SystemBit system)
    {
        WriteEntered.TrySetResult();
        WriteEntries.Release();
        WriteGate?.Wait();

        Shown.Enqueue(path);
        ShowSystem.Enqueue(system);
        if (!WritesTakeEffect) return;
        WriteSystem(path, system);
        _byPath[path] = new PathInspection(
            ActualState.Visible,
            _byPath.TryGetValue(path, out var existing) ? existing.ItemKind : Default.ItemKind);
    }

    private bool WriteSystem(string path, SystemBit system)
    {
        var was = SystemOn.TryGetValue(path, out var on) && on;
        if (system == SystemBit.Set)
            SystemOn[path] = true;
        else if (system == SystemBit.Clear)
            SystemOn[path] = false;
        return !was && system == SystemBit.Set;
    }
}
