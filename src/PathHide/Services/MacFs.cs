using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

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
    /// Resolves aliases in an existing directory path. Called only for the parent
    /// of a path being added, never the item itself, which may be a link the user
    /// means to hide.
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

    /// <summary>
    /// Carries <paramref name="source"/>'s ACL, extended attributes (Finder tags among them) and
    /// permission mode onto <paramref name="destination"/>, the replacement an atomic write is about to
    /// rename over it (content-lifecycle-conventions). Never its times or ownership. A volume that cannot
    /// hold ACLs or extended attributes keeps what it can; any other failure throws, so the original
    /// stays as it is.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static void CopyReplaceMetadata(string source, string destination)
    {
        using (var from = File.OpenHandle(source))
        using (var to = File.OpenHandle(destination, FileMode.Open, FileAccess.ReadWrite))
        {
            if (fcopyfile(from, to, IntPtr.Zero, CopyfileAcl | CopyfileXattr) < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno != ENOTSUP)
                    throw new IOException($"fcopyfile from {source} to {destination} failed (errno {errno}).");
            }
        }

        // After the extended attributes, which a read-only mode would refuse.
        File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fcopyfile(SafeFileHandle from, SafeFileHandle to, IntPtr state, uint flags);

    // From <copyfile.h>. COPYFILE_SECURITY would add COPYFILE_STAT, which copies the original's times.
    private const uint CopyfileAcl = 1 << 0;
    private const uint CopyfileXattr = 1 << 2;

    // ENOTSUP from <sys/errno.h> on macOS.
    private const int ENOTSUP = 45;

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
