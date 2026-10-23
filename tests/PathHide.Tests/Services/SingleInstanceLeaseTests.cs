using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Services;
using PathHide.Tests.Fakes;
using Xunit;

namespace PathHide.Tests.Services;

[Collection(ProcessLogCollection.Name)]
public sealed class SingleInstanceLeaseTests
{
    [MacOnlyFact]
    public void Directory_aliases_and_trailing_separators_cannot_claim_a_second_writer()
    {
        var parent = Directory.CreateTempSubdirectory("pathhide-instance-").FullName;
        var root = Directory.CreateDirectory(Path.Combine(parent, "Root")).FullName;
        var alias = Path.Combine(parent, "alias");
        Directory.CreateSymbolicLink(alias, parent);
        try
        {
            var throughParent = Path.Combine(alias, "Root");
            Assert.Equal(SingleInstanceLease.MutexName(root), SingleInstanceLease.MutexName(root + "/"));
            Assert.Equal(SingleInstanceLease.MutexName(root), SingleInstanceLease.MutexName(throughParent));
            if (Directory.Exists(root.ToUpperInvariant()))
                Assert.Equal(SingleInstanceLease.MutexName(root), SingleInstanceLease.MutexName(root.ToUpperInvariant()));
            Assert.True(SingleInstanceLease.TryAcquire(root, out var owner));
            using (owner)
            {
                SingleInstanceLease.RegisterOwnerActivationHandler(() => { });
                Assert.False(SingleInstanceLease.TryAcquire(throughParent, out var duplicate));
                Assert.Null(duplicate);
            }
        }
        finally
        {
            Directory.Delete(alias);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void A_missing_root_never_acquires_a_claim()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pathhide-missing-" + Guid.NewGuid().ToString("N"));
        Assert.ThrowsAny<IOException>(() => SingleInstanceLease.TryAcquire(missing, out _));
    }

    [Fact]
    public void SecondClaimActivatesOwnerAndCannotOpenSameRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pathhide-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var activated = new ManualResetEventSlim();
        Exception? ownerFailure = null;
        var ownerThread = new Thread(() =>
        {
            try
            {
                Assert.True(SingleInstanceLease.TryAcquire(root, out var owner));
                using (owner)
                {
                    acquired.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception ex)
            {
                ownerFailure = ex;
                acquired.Set();
            }
        });
        try
        {
            ownerThread.Start();
            Assert.True(acquired.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Null(ownerFailure);

            SingleInstanceLease.RegisterOwnerActivationHandler(activated.Set);
            Assert.False(SingleInstanceLease.TryAcquire(root, out var duplicate));
            Assert.Null(duplicate);
            Assert.True(activated.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            release.Set();
            Assert.True(ownerThread.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(ownerFailure);
            Assert.True(SingleInstanceLease.TryAcquire(root, out var successor));
            successor?.Dispose();
        }
        finally
        {
            release.Set();
            ownerThread.Join(TimeSpan.FromSeconds(10));
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TimedOutPublicationRetainsItsNativeClaimUntilThePhysicalWriteSettles()
    {
        var root = Directory.CreateTempSubdirectory("pathhide-instance-").FullName;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var contender = new Mutex(false, SingleInstanceLease.MutexName(root));
        var ownsContender = false;
        try
        {
            Assert.Throws<TimeoutException>(() => SingleInstanceLease.TryAcquire(root, out _,
                TimeSpan.FromMilliseconds(50), (path, port) =>
                {
                    entered.Set();
                    release.Wait();
                    File.WriteAllText(path, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            ownsContender = contender.WaitOne(TimeSpan.Zero);
            Assert.False(ownsContender);
        }
        finally
        {
            release.Set();
            if (!ownsContender)
                ownsContender = contender.WaitOne(TimeSpan.FromSeconds(5));
            try { Assert.True(ownsContender); }
            finally
            {
                if (ownsContender)
                    contender.ReleaseMutex();
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TimedOutPublicationReportsItsLateFailureAndReleasesItsClaim()
    {
        var root = Directory.CreateTempSubdirectory("pathhide-instance-").FullName;
        using var diagnostics = new StartupDiagnosticLog("instance: ownership worker failed after startup wait ended");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var contender = new Mutex(false, SingleInstanceLease.MutexName(root));
        var ownsContender = false;
        try
        {
            Assert.Throws<TimeoutException>(() => SingleInstanceLease.TryAcquire(root, out _,
                TimeSpan.FromMilliseconds(50), (_, _) =>
                {
                    entered.Set();
                    release.Wait();
                    throw new IOException("late endpoint sentinel");
                }));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            release.Set();
            var entry = await diagnostics.Reported.Task.WaitAsync(TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal("warn", entry.Level);
            Assert.Contains("late endpoint sentinel", entry.Error?.ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
            ownsContender = contender.WaitOne(TimeSpan.FromSeconds(5));
            try { Assert.True(ownsContender); }
            finally
            {
                if (ownsContender)
                    contender.ReleaseMutex();
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ActivationRouterRetainsAnEarlyRequest()
    {
        var router = new ActivationRequestRouter();
        var activations = 0;

        router.Request();
        router.Register(() => activations++);
        router.Request();

        Assert.Equal(2, activations);
    }
}
