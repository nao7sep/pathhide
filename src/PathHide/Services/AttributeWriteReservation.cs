using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PathHide.Services;

/// <summary>Exclude competing PathHide writes to one submitted path through physical settlement.</summary>
internal static partial class AttributeWriteReservation
{
    internal static string Name(string identity) =>
        "Global\\PathHide.AttributeWrite." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

    internal static void RunIdentity(string identity, Action write, ApplyParentProcess? parent = null,
        TimeSpan? bound = null, Action? waiting = null)
    {
        using var handle = CreateMutex(Name(identity));
        bool Acquire(TimeSpan timeout)
        {
            var result = WaitForSingleObject(handle, checked((uint)timeout.TotalMilliseconds));
            if (result == 0x102) return false;
            if (result is not (0 or 0x80)) throw Failure();
            return true;
        }
        RunReserved(Acquire, () => { if (!ReleaseMutex(handle)) throw Failure(); }, write, parent, bound, waiting);
    }

    // Shared sequencing; platform resources stay at the native edge and the portable test adapter.
    internal static void RunReserved(Func<TimeSpan, bool> acquire, Action release, Action write,
        ApplyParentProcess? parent, TimeSpan? bound, Action? waiting)
    {
        if (!acquire(TimeSpan.Zero))
        {
            waiting?.Invoke();
            if (!acquire(bound ?? BoundedVisibility.DefaultTimeout))
                throw new TimeoutException("An earlier attribute write still owns this object.");
        }
        try
        {
            parent?.RequireRunning();
            write();
        }
        finally { release(); }
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
