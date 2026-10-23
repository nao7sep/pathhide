using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PathHide.Services;

/// <summary>A no-follow leaf handle binds approval, exclusion and mutation to one filesystem object.</summary>
internal sealed partial class WindowsAttributeTarget : IDisposable
{
    private readonly SafeFileHandle _handle;
    internal string Identity { get; }

    internal WindowsAttributeTarget(string path, bool writable)
    {
        path = PathNormalizer.RequireNativeAbsolutePath(path, windows: true);
        _handle = CreateFileW(WindowsDirectoryIdentity.ToExtendedPath(path), writable ? 0x180u : 0x80u,
            7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero); // BACKUP_SEMANTICS | OPEN_REPARSE_POINT
        try
        {
            if (_handle.IsInvalid) throw Failure();
            if (!GetIdentity(_handle, 18, out var id, (uint)Marshal.SizeOf<FileId>())) throw Failure();
            Identity = $"{id.Volume:X16}:{id.Low:X16}{id.High:X16}";
        }
        catch { _handle.Dispose(); throw; }
    }

    internal bool Set(bool hide, Models.SystemBit system, string? approved, ApplyParentProcess? parent)
    {
        if (approved is not null && !string.Equals(approved, Identity, StringComparison.Ordinal))
            throw new IOException("The approved filesystem object has changed.");
        var setNow = false;
        AttributeWriteReservation.RunIdentity(Identity, () =>
        {
            if (!GetBasic(_handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>())) throw Failure();
            var current = (FileAttributes)basic.Attributes;
            if (WindowsFileVisibility.Change(current, hide, system) is not { } updated) return;
            // Zero timestamps preserve the file's times; a zero attribute mask means 'no change',
            // so NORMAL represents the absence of settable bits.
            var write = new BasicInfo { Attributes = (uint)(updated == 0 ? FileAttributes.Normal : updated) };
            if (!SetBasic(_handle, 0, ref write, (uint)Marshal.SizeOf<BasicInfo>())) throw Failure();
            setNow = !current.HasFlag(FileAttributes.System) && updated.HasFlag(FileAttributes.System);
        }, parent);
        return setNow;
    }

    public void Dispose() => _handle.Dispose();

    private static Exception Failure()
    {
        var code = Marshal.GetLastPInvokeError();
        var cause = new Win32Exception(code);
        return code == 5 ? new UnauthorizedAccessException("The filesystem object cannot be accessed.", cause)
            : new IOException("The filesystem object could not be inspected or changed.", cause);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId { internal ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo { internal long Creation, Access, Write, Change; internal uint Attributes; }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIdentity(SafeFileHandle handle, int kind, out FileId info, uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetBasic(SafeFileHandle handle, int kind, out BasicInfo info, uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetBasic(SafeFileHandle handle, int kind, ref BasicInfo info, uint size);
}
