using Contracts.Bots;

using System;

namespace ServiceClients.Bots;

/// <summary>
/// What makes a <see cref="BotAssignment"/> complete and its names safe, checked alike by the
/// Control Center before it sends one, the manager before it keeps one, and the agent before it
/// reads a file by one of its names. Names follow the one rule the Pathing Service applies too
/// (<see cref="LibraryName"/>).
/// </summary>
public static class AssignmentRules
{
    /// <summary>The longest agent id: a GUID's 32 hex digits leave room to spare.</summary>
    public const int MaxAgentIdLength = 64;

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
                if (!LibraryName.IsFileName(forever.Route))
                {
                    return $"'{forever.Route}' is not a route file name: a plain name ending in {LibraryName.Extension}";
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
                if (!LibraryName.IsRelativeName(classic.ClassProfile))
                {
                    return $"'{classic.ClassProfile}' is not a class profile name: folder/name{LibraryName.Extension} under Json/class";
                }
                if (classic.PathOverride is { } path && !LibraryName.IsRecordedPathName(path))
                {
                    return $"'{path}' is not a recorded path name: folder/name{LibraryName.Extension} under Json/path, not a Forever route";
                }
                return null;
            default:
                return $"'{assignment.Kind}' is not a kind of run";
        }
    }

    /// <summary>
    /// An agent id: letters, digits, <c>-</c> and <c>_</c>, as a file name of the manager's run history,
    /// and not a name Windows keeps for a device (<c>CON</c>, <c>NUL</c>, <c>COM1</c>...), which on a manager
    /// hosted on Windows would be the device, not a file (known issue 72).
    /// </summary>
    public static bool IsAgentId(string? agentId)
    {
        if (string.IsNullOrEmpty(agentId) || agentId.Length > MaxAgentIdLength || IsDeviceName(agentId))
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

    private static bool IsDeviceName(string name)
    {
        return name.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" ||
               name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               char.IsAsciiDigit(name[3]);
    }
}
