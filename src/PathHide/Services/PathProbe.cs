using System;
using System.IO;
using PathHide.Models;

namespace PathHide.Services;

/// <summary>
/// Classifies a path that could not be inspected directly into the state that decides
/// recovery: <see cref="ActualState.AccessDenied"/> (a permission wall a Windows elevated
/// retry may get past) versus <see cref="ActualState.Missing"/> (the path or an ancestor is
/// genuinely absent, which elevation cannot fix). The distinction is load-bearing: routing a
/// merely-absent path into an elevated retry forces a futile UAC prompt that can never
/// succeed.
///
/// A plain <see cref="Directory.Exists"/>/<see cref="Path.Exists"/> probe cannot make this
/// distinction — it swallows every failure into a bare <c>false</c>. So we probe with
/// <see cref="File.GetAttributes"/>, which throws a <em>reason-bearing</em> exception
/// (unauthorized vs. not-found), walking up the ancestor chain until a level is statable, a
/// permission wall is hit, or the root is reached.
/// </summary>
public static class PathProbe
{
    public static ActualState ClassifyInaccessible(string path)
    {
        var current = path;

        while (true)
        {
            try
            {
                // Stat this level. Succeeds for an existing, readable file or directory;
                // throws UnauthorizedAccessException at a permission wall (including a
                // parent that denies search) and a not-found exception when this level is
                // absent — the reasons Directory.Exists hides behind a bare false.
                _ = File.GetAttributes(current);

                // This level is reachable, yet a deeper level was not: the original path is
                // simply absent below an existing ancestor.
                return ActualState.Missing;
            }
            catch (UnauthorizedAccessException)
            {
                return ActualState.AccessDenied;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // This level is absent too; climb toward the nearest existing ancestor.
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent))
                    return ActualState.Missing;
                current = parent;
            }
            catch (Exception ex)
            {
                Log.Debug("reachability probe failed", ex, new { path, current });
                return ActualState.Error;
            }
        }
    }
}
