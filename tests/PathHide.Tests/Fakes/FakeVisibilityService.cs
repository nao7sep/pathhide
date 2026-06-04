using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    public void Set(string path, ActualState state, ItemKind kind = ItemKind.File)
        => _byPath[path] = new PathInspection(state, kind);

    public PathInspection Inspect(string path)
    {
        Inspected.Enqueue(path);

        var thrown = OnInspect?.Invoke(path);
        if (thrown is not null)
            throw thrown;

        return _byPath.TryGetValue(path, out var inspection) ? inspection : Default;
    }

    public void Hide(string path)
    {
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
