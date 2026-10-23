using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

/// <summary>Actual process exit and OS exclusion; every target is a disposable test file.</summary>
public sealed class AttributeWriteReservationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pathhide-write-order-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Probe : IDisposable
    {
        internal readonly Process Process;
        internal Probe(params string[] args)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(typeof(OrderingProbe.Program).Assembly.Location);
            foreach (var argument in args)
                start.ArgumentList.Add(argument);
            Process = Process.Start(start)!;
        }
        internal async Task<string> ReadAsync() =>
            await Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15))
                ?? throw new InvalidOperationException("Probe exited: " + await Process.StandardError.ReadToEndAsync());
        internal void Release() => Process.StandardInput.WriteLine("continue");
        internal async Task<int> ExitAsync()
        {
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            return Process.ExitCode;
        }
        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    Assert.True(Process.WaitForExit(15_000), "Owned probe did not exit.");
                }
            }
            finally { Process.Dispose(); }
        }
    }

    [Fact]
    public void A_reservation_whose_owner_ended_without_releasing_it_is_taken_and_the_write_runs()
    {
        // A writer that ended mid-write leaves the reservation abandoned; ownership still passes (WAIT_ABANDONED
        // on Windows, AbandonedMutexException elsewhere) and no saved value is trusted.
        var path = Path.Combine(_dir, "target");
        Mutex? abandoned = null;
        var owner = new Thread(() =>
        {
            abandoned = new Mutex(false, PathHide.OrderingProbe.Reservation.Name(path));
            Assert.True(abandoned.WaitOne(TimeSpan.FromSeconds(5)));
        });
        owner.Start();
        Assert.True(owner.Join(TimeSpan.FromSeconds(10)));

        try
        {
            var wrote = false;
            PathHide.OrderingProbe.Reservation.Run(path, () => wrote = true, bound: TimeSpan.FromSeconds(5));
            Assert.True(wrote);
        }
        finally { abandoned?.Dispose(); }
    }

    [Fact]
    public async Task Old_child_waiting_before_admission_cannot_reverse_a_restarted_GUI_write()
    {
        var path = Path.Combine(_dir, "target");
        using var gui = new Probe("parent");
        var identity = await gui.ReadAsync();
        using var child = new Probe("write", path, "old", identity, "before");
        Assert.Equal("ready", await child.ReadAsync());
        gui.Release();
        Assert.Equal(0, await gui.ExitAsync());

        using var restart = new Probe("write", path, "new", "none", "now");
        Assert.Equal("ready", await restart.ReadAsync());
        Assert.Equal("entered", await restart.ReadAsync());
        Assert.Equal(0, await restart.ExitAsync());
        child.Release();
        Assert.Equal("parent exited", await child.ReadAsync());
        Assert.Equal(4, await child.ExitAsync());
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public async Task Parent_is_rechecked_after_an_actual_contended_wait_and_unrelated_paths_remain_available()
    {
        var path = Path.Combine(_dir, "target");
        using var gui = new Probe("parent");
        var identity = await gui.ReadAsync();
        using var owner = new Probe("write", path, "owner", "none", "inside");
        Assert.Equal("ready", await owner.ReadAsync());
        Assert.Equal("entered", await owner.ReadAsync());
        using var child = new Probe("write", path, "old", identity, "now");
        Assert.Equal("ready", await child.ReadAsync());
        Assert.Equal("waiting", await child.ReadAsync());

        // A real held OS mutex proves no overlapping write, without relying on a sleep/race window.
        var wrote = false;
        Assert.Throws<TimeoutException>(() => PathHide.OrderingProbe.Reservation.Run(path, () => wrote = true,
            bound: TimeSpan.Zero));
        Assert.False(wrote);
        using var unrelated = new Probe("write", Path.Combine(_dir, "unrelated"), "independent", "none", "now");
        Assert.Equal("ready", await unrelated.ReadAsync());
        Assert.Equal("entered", await unrelated.ReadAsync());
        Assert.Equal(0, await unrelated.ExitAsync());

        gui.Release();
        Assert.Equal(0, await gui.ExitAsync());
        owner.Release();
        Assert.Equal(0, await owner.ExitAsync());
        Assert.Equal("parent exited", await child.ReadAsync());
        Assert.Equal(4, await child.ExitAsync());
        Assert.Equal("owner", File.ReadAllText(path));
    }

    [Fact]
    public async Task An_old_inflight_write_finishes_before_the_restarted_GUI_can_change_the_path()
    {
        var path = Path.Combine(_dir, "target");
        using var gui = new Probe("parent");
        var identity = await gui.ReadAsync();
        using var child = new Probe("write", path, "old", identity, "inside");
        Assert.Equal("ready", await child.ReadAsync());
        Assert.Equal("entered", await child.ReadAsync());
        gui.Release();
        Assert.Equal(0, await gui.ExitAsync());

        Assert.Throws<TimeoutException>(() => PathHide.OrderingProbe.Reservation.Run(path,
            () => File.WriteAllText(path, "new too soon"), bound: TimeSpan.Zero));
        using var restart = new Probe("write", path, "new", "none", "now");
        Assert.Equal("ready", await restart.ReadAsync());
        Assert.Equal("waiting", await restart.ReadAsync());
        child.Release();
        Assert.Equal(0, await child.ExitAsync());
        Assert.Equal("entered", await restart.ReadAsync());
        Assert.Equal(0, await restart.ExitAsync());
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public async Task A_timed_out_visibility_wait_keeps_the_actual_attribute_reservation_until_return()
    {
        var path = Path.Combine(_dir, "target");
        using var finish = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new HeldWriter(path, finish, started, settled);
        var visibility = new BoundedVisibility(service, TimeSpan.FromMilliseconds(100));
        var write = visibility.WriteAsync(path, Models.DesiredVisibility.Hidden, Models.SystemBit.Keep, CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<TimeoutException>(() => write);
            Assert.Throws<TimeoutException>(() => PathHide.OrderingProbe.Reservation.Run(path,
                () => File.WriteAllText(path, "new too soon"), bound: TimeSpan.Zero));
            PathHide.OrderingProbe.Reservation.Run(Path.Combine(_dir, "unrelated"), () => { }, bound: TimeSpan.Zero);
        }
        finally
        {
            finish.Set();
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        PathHide.OrderingProbe.Reservation.Run(path, () => File.WriteAllText(path, "new"), bound: TimeSpan.Zero);
        Assert.Equal("new", File.ReadAllText(path));
    }

    private sealed class HeldWriter(string path, ManualResetEventSlim finish,
        TaskCompletionSource started, TaskCompletionSource settled) : IVisibilityService
    {
        public PathInspection Inspect(string _) => new(Models.ActualState.Visible, Models.ItemKind.File);
        public string? ResolvePath(string _) => null;
        public bool ResolvesPaths => false;
        public void Show(string _, Models.SystemBit __) => Hide(_, __);
        public bool Hide(string _, Models.SystemBit __)
        {
            try
            {
                PathHide.OrderingProbe.Reservation.Run(path, () =>
                {
                    started.SetResult();
                    if (!finish.Wait(TimeSpan.FromSeconds(15)))
                        throw new TimeoutException("The test did not release the physical write.");
                    File.WriteAllText(path, "old");
                });
            }
            finally { settled.SetResult(); }
            return false;
        }
    }

    [Fact]
    public void A_creation_time_mismatch_never_admits_a_write()
    {
        var current = ElevatedApplyCommand.Parent.Current;
        Assert.Throws<InvalidOperationException>(() => new ApplyParentProcess(current with
        {
            StartTimeUtcTicks = current.StartTimeUtcTicks - 1,
        }));
    }

    [Fact]
    public async Task Apply_started_after_the_original_GUI_exits_changes_no_file_attributes()
    {
        var path = Path.Combine(_dir, "target");
        File.WriteAllText(path, "keep");
        var attributes = File.GetAttributes(path);
        using var gui = new Probe("parent");
        var identity = await gui.ReadAsync();
        var parent = ElevatedApplyCommand.ParseArguments(
            ["apply", "--request", "unused", "--parent", identity, "--request-sha256", new string('0', 64)])!.Parent;
        gui.Release();
        Assert.Equal(0, await gui.ExitAsync());
        var request = Path.Combine(_dir, "request.json");
        var results = Path.Combine(_dir, "results.jsonl");
        File.WriteAllText(request, ElevatedApplyCommand.SerializeRequest(new([path], [], [])));
        Assert.Equal(3, Infrastructure.ApplyChildProcess.Run(request, results, Path.Combine(_dir, "root"), parent));
        Assert.Empty(ElevatedApplyResults.Parse(File.ReadAllText(results)).Results);
        Assert.Equal(attributes, File.GetAttributes(path));
    }

    [WindowsOnlyFact]
    public async Task A_GUI_exit_stops_the_attribute_batch_instead_of_reporting_each_path_as_failed()
    {
        using var gui = new Probe("parent");
        var identity = await gui.ReadAsync();
        var invocation = ElevatedApplyCommand.ParseArguments(["apply", "--request", "unused", "--parent", identity, "--request-sha256", new string('a', 64)])!;
        using var parent = new ApplyParentProcess(invocation.Parent!);
        gui.Release();
        Assert.Equal(0, await gui.ExitAsync());

        var first = Path.Combine(_dir, "first");
        var second = Path.Combine(_dir, "second");
        File.WriteAllText(first, "keep");
        File.WriteAllText(second, "keep");
        Assert.Throws<AggregateException>(() => PathHide.Program.ApplyFileAttributes(
            [first, second], hide: true, Models.SystemBit.Keep, results: null, parent));
        Assert.Equal((FileAttributes)0, File.GetAttributes(first) & FileAttributes.Hidden);
        Assert.Equal((FileAttributes)0, File.GetAttributes(second) & FileAttributes.Hidden);
    }

    [Fact]
    public void Dot_segments_and_trailing_separators_share_the_same_reservation()
    {
        var path = Path.Combine(_dir, "target");
        Assert.Equal(PathHide.OrderingProbe.Reservation.Name(path),
            PathHide.OrderingProbe.Reservation.Name(Path.Combine(_dir, "child", "..", "target") + Path.DirectorySeparatorChar));
    }

    [WindowsOnlyFact]
    public void Windows_case_and_separator_aliases_share_the_same_reservation()
    {
        var path = Path.Combine(_dir, "target");
        Assert.Equal(PathHide.OrderingProbe.Reservation.Name(path),
            PathHide.OrderingProbe.Reservation.Name(path.ToUpperInvariant().Replace('\\', '/')));
    }
}
