using Contracts;
using Contracts.Pathing;

using ServiceClients.Routes;

using System.Linq;

using Xunit;

namespace ServiceTests.Routes;

/// <summary>
/// The route designer's model: every edit is one step that undoes and redoes, the map's handles come back
/// as one node moved, inserted or removed, and each leg keeps its kind through what changes around it.
/// </summary>
public sealed class RouteEditorTests
{
    private static readonly Point3 A = new(0, 0, 0);
    private static readonly Point3 B = new(100, 0, 0);
    private static readonly Point3 C = new(100, 100, 0);
    private static readonly Point3 D = new(0, 100, 0);

    [Fact]
    public void Every_edit_undoes_and_redoes_and_a_new_edit_drops_what_was_undone()
    {
        RouteEditor editor = new();

        editor.Add(A);
        editor.Add(B);
        editor.SetLoop(true);
        Assert.True(editor.Undo());
        Assert.False(editor.Design.Loop);
        Assert.True(editor.Undo());
        Assert.Equal([A], editor.Nodes.Select(n => n.At));
        Assert.True(editor.Redo());
        Assert.Equal([A, B], editor.Nodes.Select(n => n.At));

        editor.Add(C);
        Assert.False(editor.CanRedo);
        Assert.True(editor.CanUndo);
    }

    [Fact]
    public void An_edit_that_changes_nothing_is_no_step()
    {
        RouteEditor editor = new();
        editor.Add(A);

        editor.SetLoop(false);
        editor.SetCostZones(CostZoneUse.Apply);

        Assert.True(editor.Undo());
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void A_new_node_is_reached_by_a_leg_of_the_new_kind()
    {
        RouteEditor editor = new();
        editor.Add(A);
        editor.NewLegs = LegKind.Exact;
        editor.Add(B);
        editor.NewLegs = LegKind.Replanned;
        editor.Add(C);

        Assert.Equal([LegKind.Exact, LegKind.Replanned, LegKind.Replanned], editor.Nodes.Select(n => n.Leg));
    }

    [Fact]
    public void The_maps_handles_move_insert_and_remove_one_node_and_keep_the_rest_exactly()
    {
        Point3 precise = new(50.04f, 0.03f, 12.5f);
        RouteEditor editor = new(new RouteDesign([new RouteNode(precise, NodeKind.Required, LegKind.Exact), new RouteNode(B), new RouteNode(C)]));

        // Sent back to a tenth of a yard, with the middle one dragged.
        editor.Reshape([new Point3(50f, 0f, 12.5f), new Point3(120, 10, 0), C]);
        Assert.Equal(precise, editor.Nodes[0].At);
        Assert.Equal(new Point3(120, 10, 0), editor.Nodes[1].At);

        // A node inserted on the exact leg: both halves are exact, and the kinds stay where they were.
        editor.Reshape([new Point3(50f, 0f, 12.5f), new Point3(80, 5, 0), new Point3(120, 10, 0), C]);
        Assert.Equal([NodeKind.Required, NodeKind.Shape, NodeKind.Shape, NodeKind.Shape], editor.Nodes.Select(n => n.Kind));
        Assert.Equal([LegKind.Exact, LegKind.Exact, LegKind.Replanned, LegKind.Replanned], editor.Nodes.Select(n => n.Leg));

        editor.Reshape([new Point3(50f, 0f, 12.5f), new Point3(120, 10, 0), C]);
        Assert.Equal([precise, new Point3(120, 10, 0), C], editor.Nodes.Select(n => n.At));
    }

    [Fact]
    public void Reversed_each_leg_keeps_its_kind()
    {
        RouteEditor editor = new(new RouteDesign([new RouteNode(A, Leg: LegKind.Exact), new RouteNode(B), new RouteNode(C, Leg: LegKind.Exact)], Loop: true));

        editor.Reverse();

        // A-B exact, B-C replanned, C-A (the loop's) exact; reversed: C-B replanned, B-A exact, A-C exact.
        Assert.Equal([C, B, A], editor.Nodes.Select(n => n.At));
        Assert.Equal([LegKind.Replanned, LegKind.Exact, LegKind.Exact], editor.Nodes.Select(n => n.Leg));
    }

    [Fact]
    public void A_drawn_path_becomes_shape_nodes_on_exact_legs_and_carries_on_from_the_last_node()
    {
        RouteEditor editor = new();
        editor.Add(A);

        // Drawn from beside the last node, with a wobble the tolerance drops.
        editor.AddPath([new Point3(5, 0, 0), new Point3(50, 1, 0), B, new Point3(100, 50, 0), C]);

        Assert.Equal([A, B, C], editor.Nodes.Select(n => n.At));
        Assert.Equal([LegKind.Exact, LegKind.Exact, LegKind.Replanned], editor.Nodes.Select(n => n.Leg));
        Assert.All(editor.Nodes, n => Assert.Equal(NodeKind.Shape, n.Kind));
    }

    [Fact]
    public void A_path_drawn_away_from_the_route_is_reached_by_a_new_leg()
    {
        RouteEditor editor = new();
        editor.Add(A);

        editor.AddPath([C, D]);

        Assert.Equal([A, C, D], editor.Nodes.Select(n => n.At));
        Assert.Equal([LegKind.Replanned, LegKind.Exact, LegKind.Replanned], editor.Nodes.Select(n => n.Leg));
    }

    [Fact]
    public void An_area_is_roamed_after_the_node_nearest_its_middle()
    {
        RouteEditor editor = new(new RouteDesign([new RouteNode(A), new RouteNode(B, Leg: LegKind.Exact), new RouteNode(D)]));
        Point3[] corners = [new(90, 10, 0), new(110, 10, 0), new(100, 30, 0)];

        editor.AddArea(corners);

        RouteNode area = editor.Nodes[2];
        Assert.Equal(NodeKind.Area, area.Kind);
        Assert.Same(corners, area.Area);
        Assert.Equal(RouteEditor.DefaultKills, area.Kills);
        Assert.Equal(LegKind.Exact, area.Leg);
        Assert.Equal([A, B, area.At, D], editor.Nodes.Select(n => n.At));
    }

    [Fact]
    public void Halve_thins_shape_nodes_only_and_double_splits_each_leg_in_its_kind()
    {
        RouteEditor editor = new(new RouteDesign([new RouteNode(A), new RouteNode(B), new RouteNode(C, NodeKind.Required), new RouteNode(D),
            new RouteNode(new Point3(0, 50, 0)), new RouteNode(new Point3(0, 25, 0))]));

        editor.Halve();
        Assert.Equal([A, C, new Point3(0, 50, 0), new Point3(0, 25, 0)], editor.Nodes.Select(n => n.At));

        editor.Undo();
        editor.SetLegs(LegKind.Exact);
        editor.Double();
        Assert.Equal(11, editor.Nodes.Count);
        Assert.Equal(new Point3(50, 0, 0), editor.Nodes[1].At);
        Assert.All(editor.Nodes, n => Assert.Equal(LegKind.Exact, n.Leg));
    }

    [Fact]
    public void Heights_from_the_ground_are_no_edit_and_a_saved_design_stays_saved()
    {
        RouteEditor editor = new();
        editor.Add(A);
        editor.Add(B);
        editor.MarkSaved();

        editor.TakeHeights(i => i == 1 ? 42 : 0);

        Assert.Equal(42, editor.Nodes[1].At.Z);
        Assert.False(editor.Dirty);
        Assert.True(editor.Undo());
        Assert.Equal([A], editor.Nodes.Select(n => n.At));
        Assert.True(editor.Dirty);
    }

    [Fact]
    public void An_opened_design_is_saved_until_edited_and_can_be_undone()
    {
        RouteEditor editor = new();
        editor.Add(A);

        editor.Open(new RouteDesign([new RouteNode(B), new RouteNode(C)]));
        Assert.False(editor.Dirty);

        editor.Move(1, -1);
        Assert.Equal([C, B], editor.Nodes.Select(n => n.At));
        Assert.True(editor.Dirty);

        editor.Undo();
        editor.Undo();
        Assert.Equal([A], editor.Nodes.Select(n => n.At));
    }

    [Fact]
    public void The_legs_of_a_loop_end_back_at_the_first_node()
    {
        RouteDesign line = new([new RouteNode(A), new RouteNode(B), new RouteNode(C)]);

        Assert.Equal([(0, 1), (1, 2)], RouteEditor.Legs(line));
        Assert.Equal([(0, 1), (1, 2), (2, 0)], RouteEditor.Legs(line with { Loop = true }));
    }

    [Fact]
    public void A_bot_follows_the_nodes_and_points_along_each_exact_leg()
    {
        RouteDesign design = new([new RouteNode(A, Leg: LegKind.Exact), new RouteNode(B), new RouteNode(C, Leg: LegKind.Exact)], Loop: true);

        RoutePoint[] points = [.. RoutePoints.Of(design)];

        // A, 9 points to B (100 yd, at most 10 apart), B, C, 14 points back to A (141 yd), A again.
        Assert.Equal(1 + 9 + 1 + 1 + 14 + 1, points.Length);
        Assert.Equal([0, 1, 2], points.Where(p => p.Node >= 0).Select(p => p.Node));
        Assert.Equal(A, points[^1].At);
        Assert.All(points.Zip(points.Skip(1)).Where(p => p.First.Node != 1), p =>
            Assert.True(RouteGeometry.Distance(p.First.At, p.Second.At) <= RoutePoints.ExactSpacingYards + 0.01f));
    }
}
