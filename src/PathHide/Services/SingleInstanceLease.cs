using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PathHide.Services;

internal sealed class ActivationRequestRouter
{
    private readonly object _gate = new();
    private Action? _handler;
    private bool _pending;

    public void Register(Action handler)
    {
        var invoke = false;
        lock (_gate)
        {
            _handler = handler;
            invoke = _pending;
            _pending = false;
        }
        if (invoke)
            handler();
    }

    public void Request()
    {
        Action? handler;
        lock (_gate)
        {
            handler = _handler;
            if (handler is null)
                _pending = true;
        }
        handler?.Invoke();
    }
}

/// <summary>
/// Owns one PathHide storage root and routes a second GUI launch to the owner's window.
/// The elevated <c>apply</c> subprocess intentionally never acquires this lease.
/// </summary>
internal sealed class SingleInstanceLease : IDisposable
{
    private const string EndpointFileName = "instance.endpoint";
    private static readonly TimeSpan NotifyTimeout = TimeSpan.FromSeconds(2);
    private static readonly object CurrentGate = new();
    private static SingleInstanceLease? _current;

    private Mutex? _mutex;
    private readonly TcpListener _listener;
    private readonly Thread _listenerThread;
    private readonly ActivationRequestRouter _activationRouter = new();
    private volatile bool _disposed;
    private Thread? _ownerThread;
    private ManualResetEventSlim? _ownerStop;
    private int _disposeRequested;

    private SingleInstanceLease(Mutex mutex, TcpListener listener)
    {
        _mutex = mutex;
        _listener = listener;
        _listenerThread = new Thread(ListenForActivation)
        {
            IsBackground = true,
            Name = "PathHide activation",
        };
        _listenerThread.Start();
    }

    public static bool TryAcquire(string root, out SingleInstanceLease? lease) =>
        TryAcquire(root, out lease, BoundedStoreWork.DefaultBound, PublishEndpoint);

    // A Mutex must be acquired and released on the same OS thread. This owner retains that
    // physical claim through a timed-out endpoint write (transaction-and-external-effect-conventions).
    internal static bool TryAcquire(string root, out SingleInstanceLease? lease, TimeSpan bound,
        Action<string, int> publishEndpoint)
    {
        var stop = new ManualResetEventSlim();
        var result = new TaskCompletionSource<SingleInstanceLease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(() =>
        {
            SingleInstanceLease? acquired = null;
            try
            {
                if (!TryAcquireCore(root, out acquired, publishEndpoint))
                {
                    result.TrySetResult(null);
                    return;
                }
                acquired!._ownerThread = Thread.CurrentThread;
                acquired._ownerStop = stop;
                result.TrySetResult(acquired);
                stop.Wait();
            }
            catch (Exception ex)
            {
                if (!result.TrySetException(ex))
                    _ = Log.ReportStartup("instance: ownership worker failed after acquisition", ex,
                        warning: true);
            }
            finally
            {
                try { acquired?.DisposeCore(); }
                catch (Exception ex) { Log.Warn("instance: ownership cleanup failed", ex); }
                stop.Dispose();
            }
        }) { IsBackground = true, Name = "PathHide instance owner" };
        owner.Start();
        try
        {
            lease = result.Task.WaitAsync(bound).GetAwaiter().GetResult();
            return lease is not null;
        }
        catch (Exception waitFailure)
        {
            // A late acquisition immediately retires itself, on the same thread that owns its Mutex.
            try { stop.Set(); }
            catch (ObjectDisposedException) { }
            _ = result.Task.ContinueWith(done =>
            {
                if (done.IsFaulted)
                {
                    var error = done.Exception!;
                    if (waitFailure is TimeoutException)
                        _ = Log.ReportStartup("instance: ownership worker failed after startup wait ended", error,
                            warning: true);
                }
            }, TaskScheduler.Default);
            throw;
        }
    }

    private static bool TryAcquireCore(string root, out SingleInstanceLease? lease,
        Action<string, int> publishEndpoint)
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
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            publishEndpoint(endpointPath, port);

            lease = new SingleInstanceLease(mutex, listener);
            lock (CurrentGate)
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
            canonicalRoot = canonicalRoot.ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRoot));
        return "PathHide-" + Convert.ToHexString(hash);
    }

    public static void RegisterOwnerActivationHandler(Action handler)
    {
        SingleInstanceLease? current;
        lock (CurrentGate)
            current = _current;
        current?._activationRouter.Register(handler);
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
                    var request = Encoding.ASCII.GetBytes("activate");
                    client.GetStream().Write(request);
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
        while (!_disposed)
        {
            try
            {
                using var client = _listener.AcceptTcpClient();
                using var stream = client.GetStream();
                stream.ReadTimeout = (int)NotifyTimeout.TotalMilliseconds;
                using var reader = new StreamReader(stream, Encoding.ASCII);
                if (reader.ReadToEnd().StartsWith("activate", StringComparison.Ordinal))
                    _activationRouter.Request();
            }
            catch (SocketException) when (_disposed)
            {
                return;
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                return;
            }
            catch (IOException)
            {
                // A client can disconnect mid-request; keep the owner available
                // for the next legitimate activation rather than losing the listener.
            }
            catch (SocketException)
            {
                // A transient loopback failure is not a reason to surrender ownership.
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            return;
        try { _ownerStop!.Set(); }
        catch (ObjectDisposedException) { }
        _ownerThread!.Join(NotifyTimeout);
    }

    private void DisposeCore()
    {
        if (_disposed)
            return;
        _disposed = true;

        lock (CurrentGate)
        {
            if (ReferenceEquals(_current, this))
                _current = null;
        }

        try
        {
            _listener.Stop();
            _listenerThread.Join(NotifyTimeout);
        }
        finally
        {
            var mutex = Interlocked.Exchange(ref _mutex, null);
            if (mutex is not null)
            {
                try { mutex.ReleaseMutex(); }
                finally { mutex.Dispose(); }
            }
        }
    }
}
