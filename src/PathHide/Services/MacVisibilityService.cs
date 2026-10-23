using System;
using System.IO;
using System.Runtime.InteropServices;
using PathHide.Models;

namespace PathHide.Services;

public sealed class MacVisibilityService : IVisibilityService
{
    private readonly Func<string, FileAttributes> _readAttributes;

    public MacVisibilityService()
        : this(File.GetAttributes)
    {
    }

    /// <summary>Tests replace the attribute read, the one outbound call the item kind rests on.</summary>
    internal MacVisibilityService(Func<string, FileAttributes> readAttributes)
    {
        _readAttributes = readAttributes;
    }

    public PathInspection Inspect(string path)
    {
        try
        {
            path = PathNormalizer.RequireNativeAbsolutePath(path, windows: false);
            // One no-follow read of the entry's own attributes says both that it is there and what kind
            // it is: a link is a link whatever it points to. A missing or access-denied path throws a
            // reason-bearing exception, sorted out below; any other failure is reported once below and
            // shows the row as Error, rather than becoming a guess such as File.
            FileAttributes attributes;
            try
            {
                attributes = _readAttributes(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Distinguish a genuinely missing path/ancestor from a permission wall higher up.
                // macOS has no elevation step, but the state is still reported faithfully (the apply
                // pipeline treats AccessDenied as a terminal error here).
                return new PathInspection(PathProbe.ClassifyInaccessible(path), ItemKind.Unknown);
            }

            // Don't follow symlinks: describe the link's own flags so the result
            // matches what Hide/Show will actually modify.
            if (!MacFs.TryGetFlags(path, followSymlinks: false, out var flags))
            {
                Log.Warn("inspect: getattrlist failed", new { path, errno = Marshal.GetLastPInvokeError() });
                return new PathInspection(ActualState.Error, ItemKind.Unknown);
            }

            var hidden = (flags & MacFs.UF_HIDDEN) != 0 || IsDotPrefixed(path);
            var state = hidden ? ActualState.Hidden : ActualState.Visible;
            return new PathInspection(state, KindOf(attributes));
        }
        catch (UnauthorizedAccessException)
        {
            return new PathInspection(ActualState.AccessDenied, ItemKind.Unknown);
        }
        catch (Exception ex)
        {
            // Unexpected: the row shows Error, and the cause is kept at warn so a release build can
            // explain it.
            Log.Warn("inspect: failed", ex, new { path });
            return new PathInspection(ActualState.Error, ItemKind.Unknown);
        }
    }

    public void Hide(string path, WindowsHideMode mode)
    {
        // Per-item boundary crossing: debug, not info. The command's paths are logged once by the
        // caller's "apply: start".
        Log.Debug("hiding path", new { path });
        SetHidden(path, hidden: true);
    }

    public void Show(string path)
    {
        Log.Debug("showing path", new { path });
        SetHidden(path, hidden: false);
    }

    public bool ResolvesPaths => true;

    /// <summary>Resolve parent aliases and actual leaf case without following a final link.</summary>
    public string? ResolvePath(string path)
    {
        try { return MacFs.ResolveEntryPath(path); }
        catch (Exception ex)
        {
            Log.Debug("resolve path: failed", ex, new { path });
            return null;
        }
    }

    private static void SetHidden(string path, bool hidden)
    {
        path = PathNormalizer.RequireNativeAbsolutePath(path, windows: false);
        // Never follow a symlink. The user selected THIS path, so this path is
        // what gets hidden — and Inspect reads the same way, so the row always
        // reports the entry the user is looking at rather than something the
        // link points to.
        //
        // For a non-link the no-follow calls are identical to the following
        // ones, so there is nothing to branch on. Choosing between them from an
        // IsSymlink probe was worse than useless: the probe swallowed its own
        // failures and answered "not a link", so an unreadable or malformed
        // path silently became a chflags that set UF_HIDDEN on the link's
        // TARGET, while Inspect kept reporting the link as visible.
        //
        // Read existing flags so we don't trample unrelated bits like UF_NODUMP
        // or UF_IMMUTABLE that the user (or another tool) may have set.
        if (!MacFs.TryGetFlags(path, followSymlinks: false, out var flags))
            ThrowErrno($"getattrlist({path})");

        var updated = hidden
            ? (flags | MacFs.UF_HIDDEN)
            : (flags & ~MacFs.UF_HIDDEN);

        if (updated == flags)
            return;

        // not recorded: this changes only external filesystem metadata; paths.json
        // records the user's desired visibility and tracked-path identity.
        if (MacFs.SetFlags(path, updated, followSymlinks: false) != 0)
            ThrowErrno($"lchflags({path})");
    }

    /// <summary>
    /// Whether macOS hides this item because of its NAME rather than its flags.
    /// </summary>
    /// <remarks>
    /// macOS hides an item if it is dot-prefixed OR carries <c>UF_HIDDEN</c>.
    /// Reading only the flag made <c>~/.ssh</c> and friends — exactly the
    /// clutter this app is pointed at — report Visible for something Finder and
    /// <c>ls</c> do not show, and a Show on one then found nothing to change,
    /// re-inspected as Visible, and was counted as applied. The app cannot
    /// rename a file, so it cannot un-hide this kind; it can and must say so.
    /// </remarks>
    internal static bool IsDotPrefixed(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length > 1 && name[0] == '.';
    }

    private static void ThrowErrno(string operation)
    {
        var errno = Marshal.GetLastPInvokeError();
        var message = Marshal.GetPInvokeErrorMessage(errno);
        throw new IOException($"{operation} failed: {message} (errno {errno})");
    }

    private static ItemKind KindOf(FileAttributes attributes) =>
        attributes.HasFlag(FileAttributes.ReparsePoint) ? ItemKind.Symlink
            : attributes.HasFlag(FileAttributes.Directory) ? ItemKind.Directory
            : ItemKind.File;
}
