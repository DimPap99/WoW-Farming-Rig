namespace Contracts.Pathing;

/// <summary>
/// Whether a route is planned and walked with the authored cost zones (roads, danger zones): the
/// Classic bot always applies them; a Forever run applies them when its route file says so.
/// </summary>
public enum CostZoneUse
{
    Apply,
    Ignore
}

/// <summary>What a node of a designed route asks of the route through it.</summary>
public enum NodeKind
{
    /// <summary>
    /// Bends the route the way it was drawn; a walk that cannot get past it passes it by. A Forever
    /// route's <c>Via</c>.
    /// </summary>
    Shape,

    /// <summary>The route passes it, in order, whatever prices the legs. A Forever route's <c>To</c>.</summary>
    Required,

    /// <summary>A required place where the bot does something: visits an NPC (<see cref="StopTarget"/>). A Forever route's <c>Visit</c>.</summary>
    Stop,

    /// <summary>An area roamed until enough kills (<see cref="RouteNode.Area"/>). A Forever route's <c>Roam</c>.</summary>
    Area
}

/// <summary>How the leg from a node to the next is walked.</summary>
public enum LegKind
{
    /// <summary>The navmesh's way between its ends, found again by the bot as it walks: what the planner answers is what it walks.</summary>
    Replanned,

    /// <summary>The line as drawn, walked as it is (a Forever route's points along it, close together).</summary>
    Exact
}

/// <summary>
/// Who a <see cref="NodeKind.Stop"/> visits, the first of these it names: an NPC by name, the nearest NPC with a
/// role, or where a quest is given or handed in (by an NPC or an object). A Forever route's <c>Visit</c>.
/// </summary>
/// <param name="Npc">The NPC's name: the nearest of its spawns.</param>
/// <param name="Role">A role (an NPC type of <see cref="WorldInfo.NpcFlags"/>, such as <c>vendor</c>): the nearest who has it.</param>
/// <param name="QuestStart">A quest's id: the nearest place it is given.</param>
/// <param name="QuestEnd">A quest's id: the nearest place it is handed in.</param>
public sealed record StopTarget(string? Npc = null, string? Role = null, int? QuestStart = null, int? QuestEnd = null)
{
    /// <summary>It names someone to visit.</summary>
    public bool Named => !string.IsNullOrWhiteSpace(Npc) || !string.IsNullOrWhiteSpace(Role) || QuestStart > 0 || QuestEnd > 0;

    public override string ToString()
    {
        return !string.IsNullOrWhiteSpace(Npc) ? Npc
            : !string.IsNullOrWhiteSpace(Role) ? $"the nearest {Role}"
            : QuestStart > 0 ? $"where quest {QuestStart} is given"
            : QuestEnd > 0 ? $"where quest {QuestEnd} is handed in"
            : "nobody yet";
    }
}

/// <summary>
/// A designed route: its nodes in order, whether it loops back from the last to the first, and whether
/// it is planned and walked with the authored cost zones.
/// </summary>
public sealed record RouteDesign(RouteNode[] Nodes, bool Loop = false, CostZoneUse CostZones = CostZoneUse.Apply)
{
    public static readonly RouteDesign Empty = new([]);

    /// <summary>
    /// An empty design planned as the client's bot plans when nothing is chosen: a Forever run without the
    /// cost zones, the Classic bot with them.
    /// </summary>
    public static RouteDesign For(string? client)
    {
        return new RouteDesign([], CostZones: GameClients.IsForever(client) ? CostZoneUse.Ignore : CostZoneUse.Apply);
    }
}

/// <summary>One node of a designed route, and the leg that leaves it for the next.</summary>
/// <param name="At">World yards; a height of 0 is unknown ("find the ground here").</param>
/// <param name="Leg">How the leg to the next node is walked; the last node's, when the route loops back to the first.</param>
/// <param name="Stop">Who a <see cref="NodeKind.Stop"/> visits.</param>
/// <param name="Area">An <see cref="NodeKind.Area"/>'s corners in world yards: two opposite ones, or three or more in order.</param>
/// <param name="Kills">The kills an <see cref="NodeKind.Area"/> is roamed for.</param>
public sealed record RouteNode(Point3 At, NodeKind Kind = NodeKind.Shape, LegKind Leg = LegKind.Replanned,
    StopTarget? Stop = null, Point3[]? Area = null, int Kills = 0);
