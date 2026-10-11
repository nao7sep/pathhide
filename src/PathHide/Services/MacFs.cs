using System;
using System.Runtime.InteropServices;

namespace PathHide.Services;

/// <summary>
/// Direct bindings to the macOS BSD APIs needed for hidden-flag manipulation.
/// Calling these in-process (rather than shelling out to <c>chflags</c>/<c>stat</c>)
/// keeps TCC attribution against this app's bundle ID once the build is signed
/// and makes symlink semantics explicit at the call site instead of relying on
/// runtime <c>FileAttributes.ReparsePoint</c> detection.
/// </summary>
internal static partial class MacFs
{
    /// <summary>The Finder-hidden flag (UF_HIDDEN from sys/stat.h).</summary>
    public const uint UF_HIDDEN = 0x00008000;

    /// <summary>
    /// Reads BSD file flags via <c>getattrlist</c>. When <paramref name="followSymlinks"/>
    /// is false, returns the symlink's own flags rather than its target's.
    /// On failure, returns false; the caller can read errno via
    /// <see cref="Marshal.GetLastPInvokeError"/>.
    /// </summary>
    public static bool TryGetFlags(string path, bool followSymlinks, out uint flags)
    {
        var list = new AttrList
        {
            bitmapcount = AttrBitMapCount,
            commonattr  = AttrCmnFlags,
        };
        var result = default(FlagsResult);

        int rc = getattrlist(
            path,
            ref list,
            ref result,
            (nuint)Marshal.SizeOf<FlagsResult>(),
            followSymlinks ? 0u : FsOptNoFollow);

        if (rc != 0)
        {
            flags = 0;
            return false;
        }

        flags = result.flags;
        return true;
    }

    /// <summary>
    /// Writes BSD file flags. Returns 0 on success; otherwise -1 with errno set.
    /// When <paramref name="followSymlinks"/> is false, modifies the symlink itself
    /// rather than its target.
    /// </summary>
    public static int SetFlags(string path, uint flags, bool followSymlinks)
        => followSymlinks ? chflags(path, flags) : lchflags(path, flags);

    /// <summary>
    /// Resolves aliases in an existing directory path. Also used for the managed data root;
    /// entry identity calls it on a parent so a final symbolic link is never replaced by its target.
    /// </summary>
    public static bool TryRealPath(string path, out string resolved)
    {
        var pointer = realpath(path, IntPtr.Zero);
        if (pointer == IntPtr.Zero)
        {
            resolved = string.Empty;
            return false;
        }

        try
        {
            resolved = Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
            return resolved.Length > 0;
        }
        finally
        {
            free(pointer);
        }
    }

    /// <summary>The actual entry spelling, resolving parents but preserving a final symlink.</summary>
    internal static string? ResolveEntryPath(string path)
    {
        path = PathNormalizer.RequireNativeAbsolutePath(path, windows: false);
        var name = System.IO.Path.GetFileName(path);
        if (name is "" or "." or "..")
            return TryRealPath(path, out var directory) ? directory : null;
        var parent = System.IO.Path.GetDirectoryName(path)!;
        if (!TryRealPath(parent, out var resolvedParent))
            return null;
        // Unknown/missing entries retain their spelling. A successful no-follow lookup supplies
        // the actual leaf case on either kind of volume, including a broken symbolic link.
        var actualName = TryGetName(path);
        return System.IO.Path.Combine(resolvedParent, actualName ?? name);
    }

    private static string? TryGetName(string path)
    {
        const int capacity = 1040; // length + attrreference + NAME_MAX UTF-8 bytes and terminator
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var list = new AttrList { bitmapcount = AttrBitMapCount, commonattr = 1 }; // ATTR_CMN_NAME
            if (getattrlistName(path, ref list, buffer, capacity, FsOptNoFollow) != 0)
                return null;
            // The offset is relative to the attrreference, immediately after the length field.
            var total = Marshal.ReadInt32(buffer);
            var offset = Marshal.ReadInt32(buffer, 4);
            var length = Marshal.ReadInt32(buffer, 8);
            if (total > capacity || offset < 8 || length <= 1 || 4L + offset + length > total)
                return null;
            return Marshal.PtrToStringUTF8(buffer + 4 + offset, length - 1);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [LibraryImport("libc", EntryPoint = "getattrlist", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int getattrlistName(string path, ref AttrList attrList,
        IntPtr buffer, nuint capacity, uint options);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int chflags(string path, uint flags);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int lchflags(string path, uint flags);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int getattrlist(
        string path,
        ref AttrList attrList,
        ref FlagsResult attrBuf,
        nuint attrBufSize,
        uint options);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial IntPtr realpath(string path, IntPtr resolvedPath);

    [LibraryImport("libc")]
    private static partial void free(IntPtr pointer);

    private const ushort AttrBitMapCount = 5;
    // ATTR_CMN_FLAGS from <sys/attr.h>. Beware: 0x00000040 is ATTR_CMN_OBJPERMANENTID
    // and was a previous typo here; both compile, but only this value returns st_flags.
    private const uint   AttrCmnFlags    = 0x00040000;
    private const uint   FsOptNoFollow   = 0x00000001;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct AttrList
    {
        public ushort bitmapcount;
        public ushort reserved;
        public uint   commonattr;
        public uint   volattr;
        public uint   dirattr;
        public uint   fileattr;
        public uint   forkattr;
    }

    // getattrlist always prefixes the result buffer with a uint32 length field;
    // requesting only ATTR_CMN_FLAGS gives us [length, flags] = 8 bytes total.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct FlagsResult
    {
        public uint length;
        public uint flags;
    }
}
