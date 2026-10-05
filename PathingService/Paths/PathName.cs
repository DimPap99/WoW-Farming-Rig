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

    // Refused in any segment, on every OS, so a name means the same file everywhere.
    private static readonly SearchValues<char> ForbiddenChars = SearchValues.Create("<>:\"|?*\\");

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
    /// Whether <paramref name="segment"/> is one plain file or folder name: not empty, not
    /// <c>.</c> or <c>..</c>, no surrounding spaces, and no character that is forbidden on
    /// any OS, so a name means the same file everywhere.
    /// </summary>
    public static bool IsPlainSegment(string segment)
    {
        return segment.Length > 0 && segment is not ("." or "..") && segment.Trim() == segment &&
               segment.AsSpan().IndexOfAny(ForbiddenChars) < 0 && !HasControlChar(segment);
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
