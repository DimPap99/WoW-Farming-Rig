using ServiceClients.Bots;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BotAgent.Sync;

/// <summary>
/// The recorded paths a class profile names, read from its JSON without the bot. A profile names a
/// path file in several places: its old single path, each entry of <c>Paths</c>, a key action (an
/// NPC's vendor route), a replacement path, and the path group files listed in <c>PathsFilenames</c>,
/// whose own entries name more. Every property of those names is taken, at any depth and in any
/// letter case, as Newtonsoft (which loads profiles) matches them; a generated path names none. The
/// bot opens a name as given, with Windows separators, so <c>_pack\\1-20\\x.json</c> and
/// <c>\\_pack\\…</c> are the library's <c>_pack/1-20/x.json</c>.
/// </summary>
public static class ProfilePaths
{
    /// <summary>A path file: the profile's, a <c>Paths</c> entry's or a key action's.</summary>
    public const string PathFilename = "PathFilename";

    /// <summary>A path file that replaces <see cref="PathFilename"/>.</summary>
    public const string OverridePathFilename = "OverridePathFilename";

    /// <summary>Path group files: each a bare list of <c>Paths</c> entries, relative to the path folder.</summary>
    public const string PathsFilenames = "PathsFilenames";

    // Profiles are written by hand: comments and trailing commas are everywhere.
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>What <paramref name="json"/> names, or why it cannot be read.</summary>
    public static bool TryRead(string json, out ProfileFiles files, out string error)
    {
        List<string> paths = [];
        List<string> groups = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, Lenient);
            Walk(document.RootElement, paths, groups);
        }
        catch (JsonException ex)
        {
            files = ProfileFiles.None;
            error = ex.Message;
            return false;
        }

        List<string> skipped = [];
        files = new ProfileFiles(Library(paths, skipped), Library(groups, skipped), [.. skipped.Distinct()]);
        error = string.Empty;
        return true;
    }

    /// <summary>What the file <paramref name="file"/> names, or why it cannot be read.</summary>
    public static bool TryReadFile(string file, out ProfileFiles files, out string error)
    {
        string json;
        try
        {
            json = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files = ProfileFiles.None;
            error = ex.Message;
            return false;
        }

        return TryRead(json, out files, out error);
    }

    /// <summary>A name as the bot opens it, as the library names it: <c>/</c> between folders, none in front.</summary>
    public static string Normalize(string name)
    {
        return name.Replace('\\', '/').TrimStart('/');
    }

    private static void Walk(JsonElement element, List<string> paths, List<string> groups)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                Walk(item, paths, groups);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (Is(property, PathFilename) || Is(property, OverridePathFilename))
            {
                AddText(property.Value, paths);
            }
            else if (Is(property, PathsFilenames) && property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement group in property.Value.EnumerateArray())
                {
                    AddText(group, groups);
                }
            }
            else
            {
                Walk(property.Value, paths, groups);
            }
        }
    }

    private static bool Is(JsonProperty property, string name)
    {
        return property.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddText(JsonElement value, List<string> names)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
        {
            names.Add(text);
        }
    }

    // The names a library holds, each once; the rest (a drive, a "..") is left to the bot as given.
    private static string[] Library(List<string> names, List<string> skipped)
    {
        List<string> library = [];
        foreach (string name in names)
        {
            string normalized = Normalize(name);
            if (!AssignmentRules.IsRelativeName(normalized))
            {
                skipped.Add(name);
            }
            else if (!library.Contains(normalized, StringComparer.Ordinal))
            {
                library.Add(normalized);
            }
        }

        return [.. library];
    }
}

/// <summary>What a profile, or a path group file, names.</summary>
/// <param name="Paths">Path files, as library names.</param>
/// <param name="Groups">Path group files, as library names; each names more paths.</param>
/// <param name="Skipped">Names given that are not library names, as given; the bot opens them as it always did.</param>
public sealed record ProfileFiles(string[] Paths, string[] Groups, string[] Skipped)
{
    public static readonly ProfileFiles None = new([], [], []);
}
