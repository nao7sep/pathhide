using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PathHide.Storage;

namespace PathHide.Tests.Fakes;

/// <summary>
/// An <see cref="IRecordsReader"/> whose page reads wait until the test answers them, so a test sees
/// what the window shows while a read is out and decides how each one ends. Launches and records are
/// answered at once.
/// </summary>
public sealed class FakeRecordsReader : IRecordsReader
{
    public sealed record PendingPage(RecordsQuery Query, TaskCompletionSource<RecordsPage> Reply)
    {
        public void Answer(bool more, params RecordSummary[] records) => Reply.SetResult(new RecordsPage(records, more));

        public void Fail() => Reply.SetException(new InvalidOperationException("SQLite Error 11: 'database disk image is malformed'."));
    }

    public List<PendingPage> Pages { get; } = [];

    public IReadOnlyList<string> Sessions { get; set; } = [];

    public bool FailSessions { get; set; }

    public int SessionReads { get; private set; }

    public Dictionary<long, RecordDetail> Details { get; } = [];

    public PendingPage LastPage => Pages[^1];

    public IEnumerable<PendingPage> Unanswered => Pages.Where(page => !page.Reply.Task.IsCompleted);

    public Task<RecordsPage> ReadPageAsync(RecordsQuery query)
    {
        var pending = new PendingPage(query, new TaskCompletionSource<RecordsPage>());
        Pages.Add(pending);
        return pending.Reply.Task;
    }

    public Task<RecordDetail?> ReadDetailAsync(long id) =>
        Task.FromResult(Details.TryGetValue(id, out var record) ? record : null);

    public Task<IReadOnlyList<string>> ReadSessionsAsync()
    {
        SessionReads++;
        return FailSessions
            ? Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("sessions failed"))
            : Task.FromResult(Sessions);
    }
}

/// <summary>The search and live intervals, ended when the test says so.</summary>
public sealed class FakeDelays
{
    public List<(TimeSpan Wait, TaskCompletionSource Done)> Pending { get; } = [];

    public Task Delay(TimeSpan wait)
    {
        var done = new TaskCompletionSource();
        Pending.Add((wait, done));
        return done.Task;
    }

    /// <summary>Ends every interval still running of <paramref name="wait"/>.</summary>
    public void Elapse(TimeSpan wait)
    {
        foreach (var (_, done) in Pending.Where(pending => pending.Wait == wait && !pending.Done.Task.IsCompleted).ToList())
            done.SetResult();
    }

    public int Running(TimeSpan wait) => Pending.Count(pending => pending.Wait == wait && !pending.Done.Task.IsCompleted);
}
