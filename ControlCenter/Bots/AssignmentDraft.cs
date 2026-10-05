using Contracts.Bots;

using ServiceClients.Bots;

namespace ControlCenter.Bots;

/// <summary>
/// The assignment form's fields: a Forever route and its minutes, or a Classic profile and an
/// optional path. What is wrong with them is a choice not made yet (<see cref="Missing"/>), or the
/// manager's own reason (<see cref="AssignmentRules"/>, <see cref="Refusal"/>); either keeps it from
/// being saved. A record, so a draft compares to the one its bot's assignment makes.
/// </summary>
public sealed record AssignmentDraft
{
    public const int DefaultMinutes = 30;

    /// <summary>The data name an agent reports for a WoW: Forever client (<see cref="AgentGame.Client"/>).</summary>
    public const string ForeverClient = "forever";

    public BotRuntimeKind Kind { get; set; } = BotRuntimeKind.Forever;

    /// <summary>A route file name in the library.</summary>
    public string Route { get; set; } = string.Empty;

    public int Minutes { get; set; } = DefaultMinutes;

    /// <summary>One of the bot's class profiles.</summary>
    public string Profile { get; set; } = string.Empty;

    /// <summary>A recorded path's library name; empty for the profile's own.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The draft of <paramref name="assignment"/>, or with none a new one of the kind the bot's
    /// client plays: Classic for a Classic client, Forever otherwise.
    /// </summary>
    public static AssignmentDraft From(BotAssignment? assignment, AgentGame? game)
    {
        return assignment switch
        {
            { Kind: BotRuntimeKind.Forever, Forever: { } forever } => new AssignmentDraft
            {
                Kind = BotRuntimeKind.Forever,
                Route = forever.Route,
                Minutes = forever.Minutes,
            },
            { Kind: BotRuntimeKind.Classic, Classic: { } classic } => new AssignmentDraft
            {
                Kind = BotRuntimeKind.Classic,
                Profile = classic.ClassProfile,
                Path = classic.PathOverride ?? string.Empty,
            },
            _ => new AssignmentDraft
            {
                Kind = game is null || game.Client == ForeverClient ? BotRuntimeKind.Forever : BotRuntimeKind.Classic,
            },
        };
    }

    /// <summary>The assignment the fields make.</summary>
    public BotAssignment Build()
    {
        return Kind == BotRuntimeKind.Forever
            ? BotAssignment.ForForever(Route.Trim(), Minutes)
            : BotAssignment.ForClassic(Profile.Trim(), Path.Trim() is { Length: > 0 } path ? path : null);
    }

    /// <summary>The choice still to make, or null when the fields are filled in.</summary>
    public string? Missing => Kind switch
    {
        BotRuntimeKind.Forever when Route.Trim().Length == 0 => "Choose a route from the library.",
        BotRuntimeKind.Classic when Profile.Trim().Length == 0 => "Choose one of the bot's class profiles.",
        _ => null,
    };

    /// <summary>Why the manager would refuse the filled-in fields (<see cref="AssignmentRules.Problem"/>), or null.</summary>
    public string? Refusal => Missing is null ? AssignmentRules.Problem(Build()) : null;

    /// <summary>Whether the draft can be saved.</summary>
    public bool Valid => Missing is null && Refusal is null;
}
