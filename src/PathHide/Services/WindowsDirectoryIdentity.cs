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
        var path = ToExtendedPath(Path.GetFullPath(root));
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

    /// <summary>
    /// The extended-length spelling of a full Windows path, matching managed long-path I/O: a drive path
    /// gains <c>\\?\</c>, a share <c>\\?\UNC\</c>, and a <c>\\.\</c> device path becomes its
    /// <c>\\?\</c> form rather than a share. The resolved NT path is an identity, never a storage path.
    /// </summary>
    internal static string ToExtendedPath(string fullPath)
    {
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            return fullPath;
        if (fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            return @"\\?\" + fullPath[4..];
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
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
