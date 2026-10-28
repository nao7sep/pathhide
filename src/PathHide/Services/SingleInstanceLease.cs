using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PathHide.Services;

/// <summary>
/// Owns one PathHide storage root and routes a second GUI launch to the owner's window.
/// The elevated <c>apply</c> subprocess intentionally never acquires this lease.
/// </summary>
internal sealed class SingleInstanceLease : IDisposable
{
    private const string EndpointFileName = "instance.endpoint";
    private static readonly byte[] ActivationRequest = Encoding.ASCII.GetBytes("activate");
    private static readonly TimeSpan NotifyTimeout = TimeSpan.FromSeconds(2);
    private static SingleInstanceLease? _current;

    private Mutex? _mutex;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private Thread? _listenerThread;
    private Action? _activate;

    private SingleInstanceLease(Mutex mutex, TcpListener listener)
    {
        _mutex = mutex;
        _listener = listener;
    }

    /// <summary>
    /// Claims <paramref name="root"/> for this process, or, when another PathHide holds it, asks that one
    /// to bring its window forward and returns false. The claim is held on the calling thread, which a
    /// mutex requires, until <see cref="Dispose"/> on that same thread.
    /// </summary>
    public static bool TryAcquire(string root, out SingleInstanceLease? lease)
    {
        var endpointPath = Path.Combine(root, EndpointFileName);
        var mutex = new Mutex(initiallyOwned: false, MutexName(root));
        var ownsMutex = false;
        try
        {
            ownsMutex = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The abandoned wait transfers ownership to this process.
            ownsMutex = true;
        }

        if (!ownsMutex)
        {
            mutex.Dispose();
            NotifyPrimary(endpointPath);
            lease = null;
            return false;
        }

        TcpListener? listener = null;
        try
        {
            // Listening from here on: a second launch that connects before the window registers its
            // handler waits in the listener's backlog until then.
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            PublishEndpoint(endpointPath, ((IPEndPoint)listener.LocalEndpoint).Port);

            lease = new SingleInstanceLease(mutex, listener);
            _current = lease;
            return true;
        }
        catch
        {
            listener?.Stop();
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    internal static string MutexName(string root)
    {
        var canonicalRoot = Path.GetFullPath(root);
        if (OperatingSystem.IsWindows())
            canonicalRoot = WindowsDirectoryIdentity.Resolve(canonicalRoot).TrimEnd('\\').ToUpperInvariant();
        else if (OperatingSystem.IsMacOS())
        {
            if (!MacFs.TryRealPath(canonicalRoot, out var resolved))
                throw new IOException("The data directory's identity could not be resolved: "
                    + Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError()));
            canonicalRoot = resolved;
        }
        else
            canonicalRoot = Path.TrimEndingDirectorySeparator(canonicalRoot);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRoot));
        // The data root is shared by this user's console and remote logon sessions.
        return (OperatingSystem.IsWindows() ? @"Global\PathHide-" : "PathHide-") + Convert.ToHexString(hash);
    }

    /// <summary>
    /// Gives the claim its window's activation and starts accepting second launches, once; activations
    /// that arrived earlier are waiting for it.
    /// </summary>
    public static void RegisterOwnerActivationHandler(Action handler)
    {
        if (_current is not { } current || current._listenerThread is not null)
            return;
        current._activate = handler;
        current._listenerThread = new Thread(current.ListenForActivation)
        {
            IsBackground = true,
            Name = "PathHide activation",
        };
        current._listenerThread.Start();
    }

    private static void PublishEndpoint(string path, int port)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.ASCII.GetBytes(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private static void NotifyPrimary(string endpointPath)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < NotifyTimeout)
        {
            try
            {
                var text = File.ReadAllText(endpointPath).Trim();
                if (int.TryParse(text, out var port) && port is > 0 and <= 65535)
                {
                    using var client = new TcpClient();
                    client.Connect(IPAddress.Loopback, port);
                    client.GetStream().Write(ActivationRequest);
                    return;
                }
            }
            catch (IOException)
            {
                // The owner may still be publishing its endpoint; retry briefly.
            }
            catch (SocketException)
            {
                // A stale endpoint may still be visible during owner startup.
            }
            Thread.Sleep(20);
        }
    }

    private void ListenForActivation()
    {
        var stopping = _stopping.Token;
        var failures = 0;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using var client = _listener.AcceptTcpClient();
                if (failures > 0)
                {
                    Log.Info("instance: activation listener accepts again", new { failures });
                    failures = 0;
                }
                if (ReadsActivation(client.GetStream()))
                    _activate!();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException && stopping.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // A client can disconnect mid-request; keep the owner available
                // for the next legitimate activation rather than losing the listener.
            }
            catch (SocketException ex)
            {
                // A transient loopback failure is not a reason to surrender ownership, but one that
                // persists must not spin this thread: each retry waits twice as long, up to 5 s.
                if (failures++ == 0)
                    Log.Warn("instance: activation listener could not accept a connection; retrying", ex);
                stopping.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(100 << Math.Min(failures - 1, 6), 5000)));
            }
        }
    }

    /// <summary>
    /// Whether the connection carries the activation request. The request is a few fixed bytes, so no
    /// more than that is read, and all of it within one <see cref="NotifyTimeout"/>: a local client that
    /// streams or trickles data cannot grow memory or hold the only listener thread.
    /// </summary>
    private static bool ReadsActivation(NetworkStream stream)
    {
        var received = new byte[ActivationRequest.Length];
        var timer = Stopwatch.StartNew();
        var count = 0;
        while (count < received.Length)
        {
            var remaining = NotifyTimeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return false;
            stream.ReadTimeout = Math.Max(1, (int)remaining.TotalMilliseconds);
            var read = stream.Read(received, count, received.Length - count);
            if (read == 0)
                return false;
            count += read;
        }
        return received.AsSpan().SequenceEqual(ActivationRequest);
    }

    public void Dispose()
    {
        if (_mutex is not { } mutex)
            return;
        _mutex = null;
        if (ReferenceEquals(_current, this))
            _current = null;

        try
        {
            _stopping.Cancel();
            _listener.Stop();
            _listenerThread?.Join(NotifyTimeout);
        }
        finally
        {
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }
    }
}
