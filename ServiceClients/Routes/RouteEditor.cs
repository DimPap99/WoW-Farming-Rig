using Contracts;
using Contracts.Pathing;

using System;
using System.Collections.Generic;
using System.Linq;

namespace ServiceClients.Routes;

/// <summary>
/// A <see cref="RouteDesign"/> being edited, as the route designer edits it: each edit replaces the design
/// with a new one and can be undone and redone. It plans nothing - <see cref="RouteLegs"/> plans the
/// replanned legs - and it knows no file: <see cref="RoutePoints"/> and the writers turn a design into
/// what a bot follows. A client designing routes anywhere (the Control Center, BlazorServer) uses it.
/// </summary>
public sealed class RouteEditor
{
    /// <summary>The kills an area is roamed for unless the node says otherwise.</summary>
    public const int DefaultKills = 20;

    /// <summary>A drawn path is made sparse to its points that keep the rest within this many yards of it.</summary>
    public const float DrawnToleranceYards = 4;

    /// <summary>A drawn path that starts this close to the last node carries on from it, as one exact line.</summary>
    public const float JoinYards = 12;

    // Positions come back from the map to a tenth of a yard: a node within this of where it was has not moved.
    private const float SamePlaceYards = 0.15f;

    private readonly EditHistory<RouteDesign> history = new();
    private RouteDesign saved;

    public RouteEditor(RouteDesign? design = null)
    {
        Design = design ?? RouteDesign.Empty;
        saved = Design;
    }

    public RouteDesign Design { get; private set; }

    public IReadOnlyList<RouteNode> Nodes => Design.Nodes;

    /// <summary>The kind a new leg is: to a node added at the end, or from the route to a path drawn on.</summary>
    public LegKind NewLegs { get; set; } = LegKind.Replanned;

    public bool CanUndo => history.CanUndo;

    public bool CanRedo => history.CanRedo;

    /// <summary>The design differs from the one last opened or saved.</summary>
    public bool Dirty => !ReferenceEquals(Design, saved);

    /// <summary>The legs of <paramref name="design"/> in order, as node indices; the one back to the first last, when it loops.</summary>
    public static IEnumerable<(int From, int To)> Legs(RouteDesign design)
    {
        int count = design.Nodes.Length;
        for (int i = 0; i + 1 < count; i++)
        {
            yield return (i, i + 1);
        }

        if (design.Loop && count > 1)
        {
            yield return (count - 1, 0);
        }
    }

    /// <summary>A node at the end, reached by a leg of the <see cref="NewLegs"/> kind.</summary>
    public void Add(Point3 at, NodeKind kind = NodeKind.Shape, StopTarget? stop = null)
    {
        Change(Design with { Nodes = [.. Leading(NewLegs), new RouteNode(at, kind, NewLegs, stop)] });
    }

    /// <summary>
    /// A path drawn on the map, at the end: made sparse (<see cref="DrawnToleranceYards"/>), its points become
    /// shape nodes joined by exact legs, walked as drawn. One that starts by the last node carries on from it.
    /// </summary>
    public void AddPath(IReadOnlyList<Point3> drawn)
    {
        Point3[] path = RouteGeometry.Simplify(drawn, DrawnToleranceYards);
        if (path.Length == 0)
        {
            return;
        }

        bool joined = Design.Nodes.Length > 0 && RouteGeometry.Distance(Design.Nodes[^1].At, path[0]) <= JoinYards;
        if (joined && path.Length < 2)
        {
            return;
        }

        IEnumerable<RouteNode> added = path.Skip(joined ? 1 : 0)
            .Select((at, i) => new RouteNode(at, Leg: i < path.Length - (joined ? 2 : 1) ? LegKind.Exact : NewLegs));
        Change(Design with { Nodes = [.. Leading(joined ? LegKind.Exact : NewLegs), .. added] });
    }

    /// <summary>
    /// An area to roam, its corners two opposite ones or three or more: a node at its middle, roamed after the
    /// node nearest that, its leg the one it was put on.
    /// </summary>
    public void AddArea(Point3[] corners)
    {
        Point3 middle = RouteGeometry.Middle(corners);
        RouteNode[] nodes = Design.Nodes;
        int after = nodes.Length == 0 ? -1 : Enumerable.Range(0, nodes.Length).MinBy(i => RouteGeometry.Distance(nodes[i].At, middle));
        if (after < 0 || after == nodes.Length - 1)
        {
            Change(Design with { Nodes = [.. Leading(NewLegs), new RouteNode(middle, NodeKind.Area, NewLegs, Area: corners, Kills: DefaultKills)] });
            return;
        }

        Insert(after + 1, new RouteNode(middle, NodeKind.Area, nodes[after].Leg, Area: corners, Kills: DefaultKills));
    }

    public void Insert(int index, RouteNode node)
    {
        Change(Design with { Nodes = [.. Design.Nodes[..index], node, .. Design.Nodes[index..]] });
    }

    /// <summary>Node <paramref name="index"/> becomes <paramref name="node"/>: another kind, another leg, another place.</summary>
    public void Set(int index, RouteNode node)
    {
        RouteNode[] nodes = [.. Design.Nodes];
        nodes[index] = node;
        Change(Design with { Nodes = nodes });
    }

    /// <summary>An area node's corners moved: its node goes to their middle.</summary>
    public void SetArea(int index, Point3[] corners)
    {
        Set(index, Design.Nodes[index] with { At = RouteGeometry.Middle(corners), Area = corners });
    }

    public void Remove(int index)
    {
        Change(Design with { Nodes = [.. Design.Nodes[..index], .. Design.Nodes[(index + 1)..]] });
    }

    /// <summary>
    /// The nodes' places as the map's handles left them: one moved, one inserted on a leg (a shape node, the
    /// leg's kind on both halves), or one removed. Places within a tenth of a yard of a node's are its own.
    /// </summary>
    public void Reshape(IReadOnlyList<Point3> points)
    {
        RouteNode[] nodes = Design.Nodes;
        int first = 0;
        while (first < Math.Min(nodes.Length, points.Count) && Same(nodes[first].At, points[first]))
        {
            first++;
        }

        RouteNode[] reshaped = (points.Count - nodes.Length) switch
        {
            0 => [.. nodes.Select((node, i) => Same(node.At, points[i]) ? node : node with { At = points[i] })],
            1 => [.. nodes[..first], new RouteNode(points[first], Leg: first > 0 ? nodes[first - 1].Leg : NewLegs), .. nodes[first..]],
            -1 => [.. nodes[..first], .. nodes[(first + 1)..]],
            _ => [.. points.Select(p => new RouteNode(p, Leg: NewLegs))],
        };
        Change(Design with { Nodes = reshaped });
    }

    /// <summary>Node <paramref name="index"/> changes places with the one before or after it (<paramref name="by"/> -1 or 1).</summary>
    public void Move(int index, int by)
    {
        int other = index + by;
        if (other < 0 || other >= Design.Nodes.Length)
        {
            return;
        }

        RouteNode[] nodes = [.. Design.Nodes];
        (nodes[index], nodes[other]) = (nodes[other], nodes[index]);
        Change(Design with { Nodes = nodes });
    }

    /// <summary>
    /// Every other node goes, as a dense recording is thinned - the second, the fourth - but only a shape node:
    /// the first, the last and every other kind stay.
    /// </summary>
    public void Halve()
    {
        RouteNode[] nodes = Design.Nodes;
        List<RouteNode> kept = [];
        bool drop = false;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (drop && nodes[i].Kind == NodeKind.Shape && i < nodes.Length - 1)
            {
                drop = false;
                continue;
            }

            drop = true;
            kept.Add(nodes[i]);
        }

        Change(Design with { Nodes = [.. kept] });
    }

    /// <summary>A shape node halfway along each leg, which keeps its kind on both halves; its height is found again.</summary>
    public void Double()
    {
        RouteNode[] nodes = Design.Nodes;
        List<RouteNode> doubled = [];
        for (int i = 0; i < nodes.Length; i++)
        {
            doubled.Add(nodes[i]);
            if (i + 1 < nodes.Length)
            {
                Point3 a = nodes[i].At;
                Point3 b = nodes[i + 1].At;
                doubled.Add(new RouteNode(new Point3((a.X + b.X) / 2, (a.Y + b.Y) / 2, 0), Leg: nodes[i].Leg));
            }
        }

        Change(Design with { Nodes = [.. doubled] });
    }

    /// <summary>
    /// Nodes placed with no height take the ground's, from <paramref name="groundOf"/> (0 where it is not known yet),
    /// as a route found through them shows it. Not an edit: there is nothing to undo, and a saved design stays saved.
    /// </summary>
    public void TakeHeights(Func<int, float> groundOf)
    {
        RouteNode[] nodes = Design.Nodes;
        RouteNode[]? grounded = null;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].At.Z == 0 && groundOf(i) is float z && z != 0)
            {
                grounded ??= [.. nodes];
                grounded[i] = nodes[i] with { At = nodes[i].At with { Z = z } };
            }
        }

        if (grounded is not null)
        {
            bool wasSaved = !Dirty;
            Design = Design with { Nodes = grounded };
            saved = wasSaved ? Design : saved;
        }
    }

    /// <summary>The route walked the other way: each leg keeps its kind.</summary>
    public void Reverse()
    {
        RouteNode[] nodes = Design.Nodes;
        int count = nodes.Length;
        // Reversed node j's leg is the one that ran from node count - 2 - j to the node before it; the one back to the first stays last.
        Change(Design with
        {
            Nodes = [.. Enumerable.Range(0, count).Select(j => nodes[count - 1 - j] with { Leg = nodes[j < count - 1 ? count - 2 - j : count - 1].Leg })]
        });
    }

    public void SetLoop(bool loop)
    {
        Change(Design with { Loop = loop });
    }

    public void SetCostZones(CostZoneUse use)
    {
        Change(Design with { CostZones = use });
    }

    /// <summary>Every leg becomes <paramref name="kind"/>, and so do new ones.</summary>
    public void SetLegs(LegKind kind)
    {
        NewLegs = kind;
        Change(Design with { Nodes = [.. Design.Nodes.Select(n => n with { Leg = kind })] });
    }

    /// <summary>Another design replaces this one, as when a file is opened; it can be undone.</summary>
    public void Open(RouteDesign design)
    {
        Change(design);
        saved = Design;
    }

    /// <summary>The design as it is now is saved.</summary>
    public void MarkSaved()
    {
        saved = Design;
    }

    /// <summary>No nodes; the loop and the cost zones stay. It can be undone.</summary>
    public void Clear()
    {
        Change(Design with { Nodes = [] });
    }

    public bool Undo()
    {
        if (!history.TryUndo(Design, out RouteDesign previous))
        {
            return false;
        }

        Design = previous;
        return true;
    }

    public bool Redo()
    {
        if (!history.TryRedo(Design, out RouteDesign next))
        {
            return false;
        }

        Design = next;
        return true;
    }

    // The nodes, the last one's leg now leading on to whatever is added after it.
    private RouteNode[] Leading(LegKind leg)
    {
        RouteNode[] nodes = [.. Design.Nodes];
        if (nodes.Length > 0)
        {
            nodes[^1] = nodes[^1] with { Leg = leg };
        }

        return nodes;
    }

    private void Change(RouteDesign next)
    {
        if (next.Loop == Design.Loop && next.CostZones == Design.CostZones && next.Nodes.SequenceEqual(Design.Nodes))
        {
            return;
        }

        history.Record(Design);
        Design = next;
    }

    private static bool Same(Point3 a, Point3 b)
    {
        return MathF.Abs(a.X - b.X) <= SamePlaceYards && MathF.Abs(a.Y - b.Y) <= SamePlaceYards;
    }
}
