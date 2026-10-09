using System;
using System.IO;
using PathHide.Models;

namespace PathHide.Services;

public sealed class WindowsVisibilityService : IVisibilityService
{
    private readonly Func<WindowsHideMode> _getHideMode;

    public WindowsVisibilityService(Func<WindowsHideMode> getHideMode)
    {
        _getHideMode = getHideMode;
    }

    public PathInspection Inspect(string path)
    {
        try
        {
            // One stat for both the kind and the hidden flag. GetAttributes describes the
            // reparse point itself (it does not follow symlinks), matching what Hide/Show
            // modify. A missing or access-denied path throws a reason-bearing exception,
            // sorted out below.
            var attrs = File.GetAttributes(path);
            var hidden = attrs.HasFlag(FileAttributes.Hidden);
            var state = hidden ? ActualState.Hidden : ActualState.Visible;
            return new PathInspection(state, DetectKind(attrs));
        }
        catch (UnauthorizedAccessException)
        {
            // A permission wall on the path or an ancestor — recoverable via an elevated
            // retry on Windows (the apply pipeline routes AccessDenied there).
            return new PathInspection(ActualState.AccessDenied, ItemKind.Unknown);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Not directly statable because something in the chain is absent. Distinguish a
            // genuinely missing path/ancestor (Missing — elevation cannot help) from a
            // permission wall higher up (AccessDenied).
            return new PathInspection(PathProbe.ClassifyInaccessible(path), ItemKind.Unknown);
        }
        catch (Exception ex)
        {
            // Unexpected, unlike the missing and access-denied cases above: the row shows Error, and the
            // cause is kept at warn so a release build can explain it.
            Log.Warn("inspect: failed", ex, new { path });
            return new PathInspection(ActualState.Error, ItemKind.Unknown);
        }
    }

    public void Hide(string path)
    {
        var mode = _getHideMode();

        // Per-item boundary crossing: debug, not info. The command aggregate is
        // logged once by the caller (ApplyDesiredStateAsync).
        Log.Debug("hiding path", new { path, mode });
        WindowsFileVisibility.Set(path, hide: true, system: mode == WindowsHideMode.HiddenAndSystem);
    }

    public void Show(string path)
    {
        Log.Debug("showing path", new { path });
        WindowsFileVisibility.Set(path, hide: false, system: false);
    }

    /// <summary>
    /// Nothing to resolve: a drive or share path already compares case-insensitively, and the app
    /// operates on the path the user picked, reparse points included.
    /// </summary>
    public string? ResolveDirectory(string directory) => null;

    private static ItemKind DetectKind(FileAttributes attrs)
    {
        if (attrs.HasFlag(FileAttributes.ReparsePoint))
            return ItemKind.Symlink;

        if (attrs.HasFlag(FileAttributes.Directory))
            return ItemKind.Directory;

        return ItemKind.File;
    }
}
