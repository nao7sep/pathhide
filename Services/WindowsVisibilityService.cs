using System;
using System.IO;
using PathHide.Models;
using Serilog;

namespace PathHide.Services;

public sealed class WindowsVisibilityService : IVisibilityService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsVisibilityService>();

    private readonly Func<WindowsHideMode> _getHideMode;

    public WindowsVisibilityService(Func<WindowsHideMode> getHideMode)
    {
        _getHideMode = getHideMode;
    }

    public PathInspection Inspect(string path)
    {
        try
        {
            if (!Path.Exists(path))
            {
                if (IsAncestorAccessible(path))
                    return new PathInspection(ActualState.Missing, ItemKind.Unknown);

                return new PathInspection(ActualState.Unreachable, ItemKind.Unknown);
            }

            var kind = DetectKind(path);
            var attrs = File.GetAttributes(path);
            var hidden = attrs.HasFlag(FileAttributes.Hidden);
            var state = hidden ? ActualState.Hidden : ActualState.Visible;

            return new PathInspection(state, kind);
        }
        catch (UnauthorizedAccessException)
        {
            return new PathInspection(ActualState.Unreachable, ItemKind.Unknown);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to inspect {Path}", path);
            return new PathInspection(ActualState.Error, ItemKind.Unknown);
        }
    }

    public void Hide(string path)
    {
        var mode = _getHideMode();
        var attrs = File.GetAttributes(path);
        attrs |= FileAttributes.Hidden;

        if (mode == WindowsHideMode.HiddenAndSystem)
            attrs |= FileAttributes.System;
        else
            attrs &= ~FileAttributes.System;

        Log.Information("Hiding {Path} with mode {Mode}", path, mode);
        File.SetAttributes(path, attrs);
    }

    public void Show(string path)
    {
        var attrs = File.GetAttributes(path);
        attrs &= ~FileAttributes.Hidden;
        attrs &= ~FileAttributes.System;

        Log.Information("Showing {Path}", path);
        File.SetAttributes(path, attrs);
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
            Log.Debug(ex, "Cannot check ancestor accessibility for {Path}", path);
            return false;
        }
    }

    private static ItemKind DetectKind(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);

            if (attrs.HasFlag(FileAttributes.ReparsePoint))
                return ItemKind.Symlink;

            if (attrs.HasFlag(FileAttributes.Directory))
                return ItemKind.Directory;

            return ItemKind.File;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Cannot detect item kind for {Path}", path);
            return ItemKind.Unknown;
        }
    }
}
