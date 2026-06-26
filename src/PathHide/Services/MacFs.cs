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
