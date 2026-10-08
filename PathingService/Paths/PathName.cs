using ServiceClients;

using SharedLib.Forever;

using System;
using System.IO;

namespace PathingService.Paths;

/// <summary>
/// A recorded path's name: a library name (<see cref="LibraryName"/>, ServiceClients', the one rule the
/// manager, the Control Center and the agent check names by too), never in the Forever route folder.
/// Every name a request carries is checked here before it touches the disk, so none can reach a file
/// outside the folder - the old <c>PathController.SavePath</c> joined whatever it was given, and
/// <c>../</c> wrote anywhere.
/// </summary>
public static class PathName
{
    /// <summary>The screenshot the path recorder saves beside a path.</summary>
    public const string ScreenshotExtension = ".jpg";

    /// <summary>
    /// The file <paramref name="name"/> refers to under <paramref name="root"/>, and the name
    /// in its canonical form; false with a reason a person can act on when it is refused.
    /// </summary>
    public static bool TryResolve(string root, string? name, out string canonical, out string fullPath, out string error)
    {
        canonical = string.Empty;
        fullPath = string.Empty;
        if (LibraryName.Problem(name) is string problem)
        {
            error = problem;
            return false;
        }

        // Forever route files share the folder in a format of their own; they are not recorded paths.
        if (LibraryName.InForeverRouteFolder(name!))
        {
            error = $"{ForeverRouteFile.Folder}/ holds Forever route files, not recorded paths";
            return false;
        }

        string rootFull = Path.GetFullPath(root);
        fullPath = Path.GetFullPath(Path.Join(rootFull, name));
        if (!LibraryName.IsUnder(rootFull, fullPath))
        {
            error = $"'{name}' is outside the path folder";
            return false;
        }

        // Again on the path the OS will open, in case it rewrites a name the segments allowed.
        if (LibraryName.IsUnder(Path.Join(rootFull, ForeverRouteFile.Folder), fullPath))
        {
            error = $"{ForeverRouteFile.Folder}/ holds Forever route files, not recorded paths";
            return false;
        }

        canonical = name!;
        error = string.Empty;
        return true;
    }

    /// <summary>The name of a file under <paramref name="root"/>, as <see cref="TryResolve"/> accepts it.</summary>
    public static string Relative(string root, string fullPath)
    {
        return Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>True when a listed file belongs to the Forever route folder (<see cref="ForeverRouteFile.Folder"/>).</summary>
    public static bool IsForeverRoute(string canonical)
    {
        return LibraryName.InForeverRouteFolder(canonical);
    }

    /// <summary>
    /// <paramref name="name"/> as the files and folders already under <paramref name="root"/> spell it,
    /// when it differs from them only in case, or null: on Windows such names are one file, so a bot that
    /// synced both would run the one it did not name (known issue 71). Checked on every OS, as a service
    /// on Linux can hold both.
    /// </summary>
    public static string? StoredSpelling(string root, string name)
    {
        string[] segments = name.Split('/');
        string folder = Path.GetFullPath(root);
        for (int i = 0; i < segments.Length; i++)
        {
            bool last = i == segments.Length - 1;
            string? exact = null;
            string? other = null;
            if (Directory.Exists(folder))
            {
                foreach (string entry in last ? Directory.EnumerateFiles(folder) : Directory.EnumerateDirectories(folder))
                {
                    string stored = Path.GetFileName(entry);
                    if (stored.Equals(segments[i], StringComparison.Ordinal))
                    {
                        exact = stored;
                        break;
                    }

                    if (stored.Equals(segments[i], StringComparison.OrdinalIgnoreCase))
                    {
                        other ??= stored;
                    }
                }
            }

            if (exact is null)
            {
                if (other is null)
                {
                    return null;
                }

                segments[i] = other;
                return string.Join('/', segments);
            }

            folder = Path.Join(folder, exact);
        }

        return null;
    }

    /// <summary>The refusal of a name that is another one's but for case (<see cref="StoredSpelling"/>).</summary>
    public static string CaseTwinRefusal(string name, string stored)
    {
        return $"'{name}' is '{stored}' on Windows, where names that differ only in case are one file: save it as '{stored}'";
    }
}
