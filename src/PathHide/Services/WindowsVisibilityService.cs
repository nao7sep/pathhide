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
            Log.Debug("inspect: failed", ex, new { path });
            return new PathInspection(ActualState.Error, ItemKind.Unknown);
        }
    }

    public void Hide(string path)
    {
        var mode = _getHideMode();
        var attrs = WindowsFileVisibility.ApplyVisibility(
            File.GetAttributes(path),
            hide: true,
            system: mode == WindowsHideMode.HiddenAndSystem);

        // Per-item boundary crossing: debug, not info. The command aggregate is
        // logged once by the caller (ApplyDesiredStateAsync).
        Log.Debug("hiding path", new { path, mode });
        File.SetAttributes(path, attrs);
    }

    public void Show(string path)
    {
        var attrs = WindowsFileVisibility.ApplyVisibility(
            File.GetAttributes(path),
            hide: false,
            system: false);

        Log.Debug("showing path", new { path });
        File.SetAttributes(path, attrs);
    }

    private static ItemKind DetectKind(FileAttributes attrs)
    {
        if (attrs.HasFlag(FileAttributes.ReparsePoint))
            return ItemKind.Symlink;

        if (attrs.HasFlag(FileAttributes.Directory))
            return ItemKind.Directory;

        return ItemKind.File;
    }
}
