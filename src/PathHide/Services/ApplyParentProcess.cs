using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PathHide.Services;

/// <summary>The original GUI process, pinned before identity validation and retained through apply.</summary>
internal sealed partial class ApplyParentProcess : IDisposable
{
    private readonly Process? _process;
    private readonly SafeProcessHandle? _handle;

    internal ApplyParentProcess(ElevatedApplyCommand.Parent identity)
    {
        try
        {
            long ticks;
            if (OperatingSystem.IsWindows())
            {
                // Pin before validation; only query/synchronization rights are needed, including
                // credential elevation to a different administrator. Process.SafeHandle asks for
                // ALL_ACCESS, which is unnecessary and can deny this otherwise valid consent flow.
                _handle = OpenProcess(0x00101000, false, identity.Id);
                if (_handle.IsInvalid || !GetProcessTimes(_handle, out var creation, out _, out _, out _))
                    throw Failure();
                ticks = DateTime.FromFileTimeUtc(creation).Ticks;
            }
            else
            {
                // Native Windows handles are exercised on Windows; Mac process probes establish
                // ordering with the BCL without its SafeHandle's asynchronous Unix exit polling.
                _process = Process.GetProcessById(identity.Id);
                ticks = _process.StartTime.ToUniversalTime().Ticks;
            }
            if (ticks != identity.StartTimeUtcTicks)
                throw new InvalidOperationException("The requesting GUI process has been replaced.");
            RequireRunning();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void RequireRunning()
    {
        if (_handle is not null)
        {
            var result = WaitForSingleObject(_handle, 0);
            if (result == 0x102) // WAIT_TIMEOUT means this exact process is still running.
                return;
            if (result != 0)
                throw Failure();
        }
        else if (!_process!.HasExited)
            return;

        throw new OperationCanceledException("The requesting GUI has exited.");
    }

    public void Dispose()
    {
        _process?.Dispose();
        _handle?.Dispose();
    }

    private static Win32Exception Failure() => new(Marshal.GetLastPInvokeError());
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
