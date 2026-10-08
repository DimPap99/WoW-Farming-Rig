using Contracts.Pathing;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ServiceClients;

/// <summary>
/// The one rule for a file's name in the rig's data libraries (recorded paths, Forever routes, cost
/// zones, class profiles): relative to its library's folder, plain segments with <c>/</c> between them,
/// ending in <c>.json</c> exactly. The Pathing Service checks a name by it before the name touches the
/// disk, and the manager, the Control Center and the agent check names by it too, so all of them agree
/// on what a name is: two copies of it drifted, and an assignment could name what the service refused
/// (known issue 68), or a route be saved as <c>.JSON</c> and never listed (known issue 70).
/// </summary>
public static class LibraryName
{
    /// <summary>Every library file's extension, exactly: the libraries list <c>*.json</c>, case-sensitively on Linux.</summary>
    public const string Extension = ".json";

    /// <summary>The longest name.</summary>
    public const int MaxLength = 200;

    // Refused in any segment on every OS, so a name means the same file everywhere; and %, as a name
    // whose / came escaped (%2F, as Swagger UI sends a whole name) would be one flat file.
    private static readonly SearchValues<char> ForbiddenChars = SearchValues.Create("<>:\"|?*\\%");

    private static readonly StringComparison FileSystemCase =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Why <paramref name="name"/> is not a library name, in words a person can act on; null when it is one.</summary>
    public static string? Problem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "a name is required";
        }

        if (name.Length > MaxLength)
        {
            return $"a name is at most {MaxLength} characters";
        }

        if (name.StartsWith('/') || Path.IsPathRooted(name))
        {
            return $"'{name}' must be relative to its folder";
        }

        foreach (string segment in name.Split('/'))
        {
            if (!IsPlainSegment(segment))
            {
                return $"'{name}' is not a valid name: use folder/name{Extension} with plain file names";
            }
        }

        return name.Length > Extension.Length && name.EndsWith(Extension, StringComparison.Ordinal)
            ? null
            : $"'{name}' must end in {Extension}";
    }

    /// <summary>A name relative to a library's folder, which cannot leave it: <see cref="Problem"/> finds nothing.</summary>
    public static bool IsRelativeName(string? name)
    {
        return Problem(name) is null;
    }

    /// <summary>One plain file name: no folders.</summary>
    public static bool IsFileName(string? name)
    {
        return IsRelativeName(name) && !name!.Contains('/');
    }

    /// <summary>A recorded path's name: a library name outside the Forever route folder, whose files are another format.</summary>
    public static bool IsRecordedPathName(string? name)
    {
        return IsRelativeName(name) && !InForeverRouteFolder(name!);
    }

    /// <summary>Whether <paramref name="name"/> lies in the path library's Forever route folder, in any case.</summary>
    public static bool InForeverRouteFolder(string name)
    {
        return name.StartsWith(SyncSets.ForeverRouteFolder + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="segment"/> is one plain file or folder name: not empty, no surrounding spaces,
    /// not ending in a dot (so not <c>.</c> or <c>..</c> either), and no character any OS forbids. Windows
    /// drops a trailing dot, so <c>_forever./x.json</c> would open <c>_forever\x.json</c> there.
    /// </summary>
    public static bool IsPlainSegment(string segment)
    {
        if (segment.Length == 0 || segment.EndsWith('.') || segment.Trim() != segment || segment.AsSpan().IndexOfAny(ForbiddenChars) >= 0)
        {
            return false;
        }

        foreach (char c in segment)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The files of the library at <paramref name="folder"/> (its subfolders too when <paramref name="nested"/>) that a request
    /// could name, with their names, as every listing gives them: by name, ignoring case. A file no request could name is not
    /// offered, since nothing could open, save, sync or assign it; on Windows that leaves out <c>.JSON</c> too.
    /// </summary>
    public static IReadOnlyList<(string Name, string File)> Files(string folder, bool nested)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(folder, "*" + Extension, nested ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Select(file => (Name: Relative(folder, file), File: file))
            .Where(entry => IsRelativeName(entry.Name))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A file's name in the library at <paramref name="folder"/>: relative to it, <c>/</c> between folders.</summary>
    public static string Relative(string folder, string file)
    {
        return Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>True when <paramref name="fullPath"/> lies inside <paramref name="root"/> (both full paths), as this OS compares them.</summary>
    public static bool IsUnder(string root, string fullPath)
    {
        return fullPath.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, FileSystemCase);
    }
}
