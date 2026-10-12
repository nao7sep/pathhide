using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PathHide.Services;

/// <summary>Exclude competing PathHide writes to one submitted path through physical settlement.</summary>
internal static partial class AttributeWriteReservation
{
    internal static string Name(string path)
    {
        // Match the Windows writer's lexical path contract, without another filesystem query.
        // GetFullPath removes dot segments; family normalization retains POSIX case in probes.
        var full = Path.GetFullPath(path);
        if (!PathNormalizer.TryNormalize(full, out var normalized, out var family))
            throw new ArgumentException("The write path is not absolute.", nameof(path));
        var key = PathNormalizer.IdentityKey(normalized, family);
        return "Global\\PathHide.AttributeWrite." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    internal static void Run(string path, Action write, ApplyParentProcess? parent = null,
        TimeSpan? bound = null, Action? waiting = null)
    {
        var name = Name(path);
        var wait = bound ?? BoundedVisibility.DefaultTimeout;
        if (!OperatingSystem.IsWindows())
        {
            // Isolated cross-process tests exercise the same ownership/check order on the Mac.
            using var mutex = new Mutex(false, name);
            try
            {
                if (!mutex.WaitOne(TimeSpan.Zero))
                {
                    waiting?.Invoke(); // Test probes observe actual contention before releasing a producer.
                    if (!mutex.WaitOne(wait))
                        throw new TimeoutException("An earlier attribute write still owns this path.");
                }
            }
            catch (AbandonedMutexException) { /* ownership was acquired; no saved value is trusted */ }
            try
            {
                parent?.RequireRunning();
                write();
            }
            finally { mutex.ReleaseMutex(); }
            return;
        }

        using var handle = CreateMutex(name);
        var result = WaitForSingleObject(handle, 0);
        if (result == 0x102)
        {
            waiting?.Invoke();
            result = WaitForSingleObject(handle, checked((uint)wait.TotalMilliseconds));
        }
        if (result == 0x102) // WAIT_TIMEOUT: no ownership, and no write.
            throw new TimeoutException("An earlier attribute write still owns this path.");
        if (result is not (0 or 0x80)) // WAIT_OBJECT_0 / WAIT_ABANDONED both grant ownership.
            throw Failure();
        try
        {
            // This must follow the wait. A process check made before it can become stale while an
            // older write stalls. If the GUI exits after this check, exclusion lasts through Set.
            parent?.RequireRunning();
            write();
        }
        finally
        {
            if (!ReleaseMutex(handle))
                throw Failure();
        }
    }

    private static SafeWaitHandle CreateMutex(string name)
    {
        // Consent can use another administrator account. Only synchronization access is shared:
        // this mutex orders cooperative writes, not authentication or access to the target file.
        // SYNCHRONIZE suffices to acquire/release a mutex; unlike ALL_ACCESS it needs no ACL edits.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(A;;0x00100000;;;AU)", 1,
                out var descriptor, out _))
            throw Failure();
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = descriptor,
            };
            var handle = CreateMutexExW(ref attributes, name, 0, 0x00100000);
            if (!handle.IsInvalid)
                return handle;
            var failure = Failure();
            handle.Dispose();
            throw failure;
        }
        finally { _ = LocalFree(descriptor); }
    }

    private static IOException Failure() => new("Attribute write ownership could not be established.",
        new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal uint Length;
        internal IntPtr Descriptor;
        internal int InheritHandle;
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision,
        out IntPtr descriptor, out uint size);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeWaitHandle CreateMutexExW(ref SecurityAttributes attributes, string name,
        uint flags, uint access);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseMutex(SafeWaitHandle handle);
    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr pointer);
}
