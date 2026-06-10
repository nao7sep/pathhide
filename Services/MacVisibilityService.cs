using System;
using System.IO;
using System.Runtime.InteropServices;
using PathHide.Models;

namespace PathHide.Services;

public sealed class MacVisibilityService : IVisibilityService
{
    public PathInspection Inspect(string path)
    {
        try
        {
            if (!Path.Exists(path))
            {
                return IsAncestorAccessible(path)
                    ? new PathInspection(ActualState.Missing, ItemKind.Unknown)
                    : new PathInspection(ActualState.Unreachable, ItemKind.Unknown);
            }

            // Don't follow symlinks: describe the link's own flags so the result
            // matches what Hide/Show will actually modify.
            if (!MacFs.TryGetFlags(path, followSymlinks: false, out var flags))
            {
                Log.Error("inspect: getattrlist failed", new { path, errno = Marshal.GetLastPInvokeError() });
                return new PathInspection(ActualState.Error, ItemKind.Unknown);
            }

            var hidden = (flags & MacFs.UF_HIDDEN) != 0;
            var state = hidden ? ActualState.Hidden : ActualState.Visible;
            return new PathInspection(state, DetectKind(path));
        }
        catch (UnauthorizedAccessException)
        {
            return new PathInspection(ActualState.Unreachable, ItemKind.Unknown);
        }
        catch (Exception ex)
        {
            Log.Error("inspect: failed", ex, new { path });
            return new PathInspection(ActualState.Error, ItemKind.Unknown);
        }
    }

    public void Hide(string path)
    {
        // Per-item boundary crossing: debug, not info. The aggregate of a hide/show
        // command is logged once by the caller (ApplyDesiredStateAsync).
        Log.Debug("hiding path", new { path });
        SetHidden(path, hidden: true);
    }

    public void Show(string path)
    {
        Log.Debug("showing path", new { path });
        SetHidden(path, hidden: false);
    }

    private static void SetHidden(string path, bool hidden)
    {
        var operateOnLink = IsSymlink(path);

        // Read existing flags so we don't trample unrelated bits like UF_NODUMP
        // or UF_IMMUTABLE that the user (or another tool) may have set.
        if (!MacFs.TryGetFlags(path, followSymlinks: !operateOnLink, out var flags))
            ThrowErrno($"getattrlist({path})");

        var updated = hidden
            ? (flags | MacFs.UF_HIDDEN)
            : (flags & ~MacFs.UF_HIDDEN);

        if (updated == flags)
            return;

        if (MacFs.SetFlags(path, updated, followSymlinks: !operateOnLink) != 0)
            ThrowErrno($"chflags({path})");
    }

    private static void ThrowErrno(string operation)
    {
        var errno = Marshal.GetLastPInvokeError();
        var message = Marshal.GetPInvokeErrorMessage(errno);
        throw new IOException($"{operation} failed: {message} (errno {errno})");
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            // FileInfo/DirectoryInfo.LinkTarget is the link-aware probe; unlike
            // File.GetAttributes it doesn't follow the symlink before answering.
            return new FileInfo(path).LinkTarget is not null
                || new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception ex)
        {
            Log.Debug("symlink probe failed", ex, new { path });
            return false;
        }
    }

    private static bool IsAncestorAccessible(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
            return false;

        try
        {
            return Directory.Exists(parent);
        }
        catch (Exception ex)
        {
            Log.Debug("ancestor probe failed", ex, new { path });
            return false;
        }
    }

    private static ItemKind DetectKind(string path)
    {
        try
        {
            if (IsSymlink(path))
                return ItemKind.Symlink;

            return Directory.Exists(path) ? ItemKind.Directory : ItemKind.File;
        }
        catch (Exception ex)
        {
            Log.Debug("kind probe failed", ex, new { path });
            return ItemKind.Unknown;
        }
    }
}
