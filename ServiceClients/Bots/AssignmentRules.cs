using Contracts.Bots;

using System;
using System.Buffers;

namespace ServiceClients.Bots;

/// <summary>
/// What makes a <see cref="BotAssignment"/> complete and its names safe, checked alike by the
/// Control Center before it sends one, the manager before it keeps one, and the agent before it
/// reads a file by one of its names.
/// </summary>
public static class AssignmentRules
{
    public const string FileExtension = ".json";

    /// <summary>The longest name a file reference may have.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The longest agent id: a GUID's 32 hex digits leave room to spare.</summary>
    public const int MaxAgentIdLength = 64;

    // Refused in any segment, on every OS, so a name means the same file everywhere.
    private static readonly SearchValues<char> ForbiddenChars = SearchValues.Create("<>:\"|?*\\");

    /// <summary>Why <paramref name="assignment"/> cannot run, or null.</summary>
    public static string? Problem(BotAssignment? assignment)
    {
        switch (assignment)
        {
            case null:
                return "an assignment is required";
            case { Kind: BotRuntimeKind.Forever }:
                if (assignment.Forever is not { } forever || assignment.Classic is not null)
                {
                    return "a Forever assignment carries a route and its minutes, and nothing else";
                }
                if (!IsFileName(forever.Route))
                {
                    return $"'{forever.Route}' is not a route file name: a plain name ending in {FileExtension}";
                }
                if (forever.Minutes is < 1 or > BotManagerApi.MaxMinutes)
                {
                    return $"a Forever run is bounded by 1 to {BotManagerApi.MaxMinutes} minutes";
                }
                return null;
            case { Kind: BotRuntimeKind.Classic }:
                if (assignment.Classic is not { } classic || assignment.Forever is not null)
                {
                    return "a Classic assignment carries a class profile and an optional path, and nothing else";
                }
                if (!IsRelativeName(classic.ClassProfile))
                {
                    return $"'{classic.ClassProfile}' is not a class profile name: folder/name{FileExtension} under Json/class";
                }
                if (classic.PathOverride is { } path && !IsRelativeName(path))
                {
                    return $"'{path}' is not a recorded path name: folder/name{FileExtension} under Json/path";
                }
                return null;
            default:
                return $"'{assignment.Kind}' is not a kind of run";
        }
    }

    /// <summary>One plain file name ending in <c>.json</c>: no folders.</summary>
    public static bool IsFileName(string? name)
    {
        return IsRelativeName(name) && !name!.Contains('/');
    }

    /// <summary>
    /// A file name relative to a data folder: plain segments with <c>/</c> between them, never
    /// <c>.</c> or <c>..</c>, ending in <c>.json</c>; it cannot leave the folder it is read from.
    /// </summary>
    public static bool IsRelativeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength || name.Length <= FileExtension.Length ||
            !name.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase) || name.StartsWith('/'))
        {
            return false;
        }

        foreach (string segment in name.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Trim() != segment ||
                segment.AsSpan().IndexOfAny(ForbiddenChars) >= 0 || HasControlChar(segment))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An agent id: letters, digits, <c>-</c> and <c>_</c>, as a file name of the manager's run history.</summary>
    public static bool IsAgentId(string? agentId)
    {
        if (string.IsNullOrEmpty(agentId) || agentId.Length > MaxAgentIdLength)
        {
            return false;
        }

        foreach (char c in agentId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
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
