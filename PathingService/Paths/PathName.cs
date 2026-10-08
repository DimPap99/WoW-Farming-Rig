using SharedLib.Forever;

using System;
using System.Buffers;
using System.IO;

namespace PathingService.Paths;

/// <summary>
/// A recorded path's name: relative to the path folder, <c>/</c> between subfolders, ending
/// in <c>.json</c>. Every name a request carries is checked here before it touches the disk,
/// so none can reach a file outside the folder - the old <c>PathController.SavePath</c>
/// joined whatever it was given, and <c>../</c> wrote anywhere.
/// </summary>
public static class PathName
{
    public const string Extension = ".json";

    /// <summary>The screenshot the path recorder saves beside a path.</summary>
    public const string ScreenshotExtension = ".jpg";

    public const int MaxLength = 200;

    // Refused in any segment, on every OS, so a name means the same file everywhere; and %, as a
    // name whose / came escaped (%2F, as Swagger UI sends a whole name) would save as one flat file.
    private static readonly SearchValues<char> ForbiddenChars = SearchValues.Create("<>:\"|?*\\%");

    private static readonly StringComparison FileSystemCase =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The file <paramref name="name"/> refers to under <paramref name="root"/>, and the name
    /// in its canonical form; false with a reason a person can act on when it is refused.
    /// </summary>
    public static bool TryResolve(string root, string? name, out string canonical, out string fullPath, out string error)
    {
        canonical = string.Empty;
        fullPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "a path name is required";
            return false;
        }

        if (name.Length > MaxLength)
        {
            error = $"a path name is at most {MaxLength} characters";
            return false;
        }

        if (name.StartsWith('/') || Path.IsPathRooted(name))
        {
            error = $"'{name}' must be relative to the path folder";
            return false;
        }

        string[] segments = name.Split('/');
        foreach (string segment in segments)
        {
            if (!IsPlainSegment(segment))
            {
                error = $"'{name}' is not a valid path name: use folder/name.json with plain file names";
                return false;
            }
        }

        if (!name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            error = $"'{name}' must end in {Extension}";
            return false;
        }

        // Forever route files share the folder in a format of their own; they are not recorded paths.
        if (segments.Length > 1 && segments[0].Equals(ForeverRouteFile.Folder, StringComparison.OrdinalIgnoreCase))
        {
            error = $"{ForeverRouteFile.Folder}/ holds Forever route files, not recorded paths";
            return false;
        }

        string rootFull = Path.GetFullPath(root);
        fullPath = Path.GetFullPath(Path.Join(rootFull, name));
        if (!IsUnder(rootFull, fullPath))
        {
            error = $"'{name}' is outside the path folder";
            return false;
        }

        // Again on the path the OS will open, in case it rewrites a name the segments allowed.
        if (IsUnder(Path.Join(rootFull, ForeverRouteFile.Folder), fullPath))
        {
            error = $"{ForeverRouteFile.Folder}/ holds Forever route files, not recorded paths";
            return false;
        }

        canonical = name;
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
        return canonical.StartsWith(ForeverRouteFile.Folder + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="segment"/> is one plain file or folder name: not empty, no
    /// surrounding spaces, not ending in a dot (so not <c>.</c> or <c>..</c> either), and no
    /// character that is forbidden on any OS, so a name means the same file everywhere. Windows
    /// drops a trailing dot, so <c>_forever./x.json</c> would open <c>_forever\x.json</c> there.
    /// </summary>
    public static bool IsPlainSegment(string segment)
    {
        return segment.Length > 0 && !segment.EndsWith('.') && segment.Trim() == segment &&
               segment.AsSpan().IndexOfAny(ForbiddenChars) < 0 && !HasControlChar(segment);
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

    /// <summary>True when <paramref name="fullPath"/> lies inside <paramref name="root"/> (both full paths).</summary>
    public static bool IsUnder(string root, string fullPath)
    {
        return fullPath.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, FileSystemCase);
    }

    private static bool HasControlChar(string segment)
    {
        foreach (char c in segment)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }
}
