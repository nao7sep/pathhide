using System;
using System.Globalization;
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
    /// <summary>
    /// Tries to claim <paramref name="root"/> as another launch would. The claim is a mutex the owning
    /// thread holds, and that thread would claim it again, so the attempt runs on a thread of its own.
    /// </summary>
    internal static bool ClaimFromAnotherThread(string root)
    {
        var claimed = false;
        Exception? failure = null;
        var other = new Thread(() =>
        {
            try
            {
                claimed = SingleInstanceLease.TryAcquire(root, out var lease);
                lease?.Dispose();
            }
            catch (Exception ex) { failure = ex; }
        });
        other.Start();
        other.Join();
        if (failure is not null)
            throw failure;
        return claimed;
    }

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
                Assert.False(ClaimFromAnotherThread(throughParent));
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
                    release.Wait();
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
            acquired.Wait(TestContext.Current.CancellationToken);
            Assert.Null(ownerFailure);

            // A second launch that arrives before the window registers waits for it.
            Assert.False(SingleInstanceLease.TryAcquire(root, out var duplicate));
            Assert.Null(duplicate);
            SingleInstanceLease.RegisterOwnerActivationHandler(activated.Set);
            activated.Wait(TestContext.Current.CancellationToken);

            release.Set();
            ownerThread.Join();
            Assert.Null(ownerFailure);
            Assert.True(SingleInstanceLease.TryAcquire(root, out var successor));
            successor?.Dispose();
        }
        finally
        {
            release.Set();
            ownerThread.Join();
            Directory.Delete(root, recursive: true);
        }
    }

    // Anything local can connect to the activation port. One that keeps sending must neither grow the
    // owner's memory nor hold its only listener: the next launch still brings the window forward.
    [Fact]
    public void A_connection_that_keeps_sending_does_not_keep_a_second_launch_from_activating_the_owner()
    {
        var root = Path.Combine(Path.GetTempPath(), "pathhide-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var activated = new ManualResetEventSlim();
        using var stopSending = new ManualResetEventSlim();
        Thread? sender = null;
        Assert.True(SingleInstanceLease.TryAcquire(root, out var owner));
        try
        {
            SingleInstanceLease.RegisterOwnerActivationHandler(activated.Set);
            var port = int.Parse(File.ReadAllText(Path.Combine(root, "instance.endpoint")).Trim(), CultureInfo.InvariantCulture);
            var hog = new System.Net.Sockets.TcpClient();
            hog.Connect(System.Net.IPAddress.Loopback, port);
            sender = new Thread(() =>
            {
                using (hog)
                {
                    try
                    {
                        while (!stopSending.Wait(100))
                            hog.GetStream().Write("xxxxxxxx"u8);
                    }
                    catch (IOException)
                    {
                        // The owner stopped reading this connection, as it should.
                    }
                }
            }) { IsBackground = true };
            sender.Start();

            Assert.False(ClaimFromAnotherThread(root));
            activated.Wait(TestContext.Current.CancellationToken);
        }
        finally
        {
            stopSending.Set();
            sender?.Join();
            owner?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
