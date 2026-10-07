using ServiceClients.Pathing;

using System.Numerics;

using Xunit;

namespace ServiceTests.Clients;

/// <summary>The editing model the recorder (BlazorServer) and the editor (Control Center) share.</summary>
public sealed class PathEditorTests
{
    private static readonly Vector3 A = new(10, 10, 0);
    private static readonly Vector3 B = new(20, 10, 0);
    private static readonly Vector3 C = new(30, 10, 0);

    [Fact]
    public void Recording_keeps_a_point_only_past_the_spacing_in_scaled_map_units()
    {
        PathEditor editor = new([]);

        Assert.True(editor.TryRecord(new Vector3(50f, 50f, 0), 60));
        // 0.5 map units apart is 50 scaled units: under a spacing of 60.
        Assert.False(editor.TryRecord(new Vector3(50.5f, 50f, 0), 60));
        Assert.True(editor.TryRecord(new Vector3(50.7f, 50f, 0), 60));
        Assert.Equal(2, editor.Count);
    }

    [Fact]
    public void Moving_a_point_moves_every_equal_point()
    {
        // There and back: B appears twice.
        PathEditor editor = new([A, B, C, B]);

        editor.Select(B, 1);
        Vector3 moved = new(21, 11, 0);
        editor.MoveSelectedTo(moved);

        Assert.Equal([A, moved, C, moved], editor.ToArray());
        Assert.Equal(moved, editor.Selected);
    }

    [Fact]
    public void Removing_selects_the_point_now_in_its_place()
    {
        PathEditor editor = new([A, B, C]);

        editor.Select(B, 1);
        editor.RemoveSelected();

        Assert.Equal([A, C], editor.ToArray());
        Assert.Equal(C, editor.Selected);
        Assert.Equal(B, editor.Preview);
    }

    [Fact]
    public void Inserting_after_the_selection_selects_the_new_point_on_the_ground()
    {
        PathEditor editor = new([A, C]);

        editor.Select(A, 0);
        editor.InsertAfterSelected(new Vector3(15, 12, 99));

        Assert.Equal([A, new Vector3(15, 12, 0), C], editor.ToArray());
        Assert.Equal(new Vector3(15, 12, 0), editor.Selected);
    }

    [Fact]
    public void Halving_and_doubling_the_points()
    {
        PathEditor editor = new([A, B, C]);

        editor.Double();
        Assert.Equal([A, new Vector3(15, 10, 0), B, new Vector3(25, 10, 0), C], editor.ToArray());

        editor.Halve();
        Assert.Equal([A, B, C], editor.ToArray());
    }

    // Phase 2 review, known issue 56: after Halve the selected point stood at a smaller place, and the
    // search for it went on from the old one: Previous and Next did nothing, Remove cleared the selection.
    [Fact]
    public void The_selection_survives_a_halving()
    {
        Vector3 d = new(40, 10, 0);
        Vector3 e = new(50, 10, 0);
        PathEditor editor = new([A, B, C, d, e]);
        editor.Select(e, 4);

        editor.Halve();

        Assert.Equal([A, C, e], editor.ToArray());
        Assert.Equal(e, editor.Selected);
        Assert.True(editor.TryGetNeighbour(-1, out Vector3 previous, out int index));
        Assert.Equal(C, previous);
        Assert.Equal(1, index);

        // A selected point that the halving drops hands the selection to the one kept before it.
        editor.Double();
        editor.Select(editor.Points[1], 1);
        editor.Halve();
        Assert.Equal([A, C, e], editor.ToArray());
        Assert.Equal(A, editor.Selected);

        editor.Select(C, 1);
        editor.RemoveSelected();
        Assert.Equal(e, editor.Selected);
    }

    [Fact]
    public void Neighbours_stop_at_the_ends()
    {
        PathEditor editor = new([A, B, C]);
        editor.Select(C, 2);

        Assert.True(editor.TryGetNeighbour(1, out Vector3 next, out int index));
        Assert.Equal(C, next);
        Assert.Equal(2, index);
        Assert.True(editor.TryGetNeighbour(-1, out Vector3 previous, out _));
        Assert.Equal(B, previous);
    }
}
