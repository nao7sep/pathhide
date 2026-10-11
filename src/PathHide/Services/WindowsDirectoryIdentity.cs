using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PathHide.Services;

/// <summary>Resolve the managed directory for exclusion only, following directory aliases.</summary>
internal static partial class WindowsDirectoryIdentity
{
    internal static unsafe string Resolve(string root)
    {
        var path = Path.GetFullPath(root);
        // Match managed long-path I/O. The returned NT path is an identity, never a storage path.
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        using var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw Failure();

        var buffer = new char[512];
        while (true)
        {
            uint size;
            fixed (char* pointer = buffer)
                size = GetFinalPathNameByHandleW(handle, pointer, (uint)buffer.Length, 2);
            if (size == 0)
                throw Failure();
            if (size < buffer.Length)
                return new string(buffer, 0, (int)size);
            buffer = new char[checked((int)size + 1)];
        }
    }

    private static IOException Failure() => new("The data directory's identity could not be resolved.",
        new Win32Exception(Marshal.GetLastPInvokeError()));

    // Zero-access directory handle, share read/write/delete, OPEN_EXISTING, BACKUP_SEMANTICS.
    // No OPEN_REPARSE_POINT: exclusion must follow the final directory link too.
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    // FILE_NAME_NORMALIZED | VOLUME_NAME_NT. Failure has no textual fallback that admits an alias.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle handle,
        char* buffer, uint capacity, uint flags);
}
