using System.Globalization;
using System.Numerics;

namespace SharedLib.Forever;

/// <summary>
/// A point on a zone map in the coordinates a player reads, 0 to 100 across
/// the map, as upstream's recorded routes store them.
/// </summary>
public readonly record struct ForeverMapPoint(int UIMapId, double X, double Y)
{
    /// <summary>Map coordinates run from 0 to this.</summary>
    public const double MapSize = 100;

    /// <summary>
    /// The point in world yards, with the instance map its world positions
    /// report, or false with why. Any zone in the map data converts; whether
    /// the player can walk there is the start-up check's to say.
    /// </summary>
    public bool TryToWorld(WorldMapAreaDB maps, out Vector2 world, out int instanceMapId, out string reason)
    {
        world = default;
        instanceMapId = 0;
        if (!double.IsFinite(X) || !double.IsFinite(Y) || X is < 0 or > MapSize || Y is < 0 or > MapSize)
        {
            reason = $"the map point {this} is outside 0 to {MapSize}";
            return false;
        }
        if (!maps.TryGet(UIMapId, out WorldMapArea area))
        {
            reason = $"map {UIMapId} of the point {this} is not in the map data";
            return false;
        }
        Vector3 point = WorldMapAreaDB.ToWorld_FlipXY(new Vector3((float)X, (float)Y, 0), area);
        world = new(point.X, point.Y);
        instanceMapId = area.MapID;
        reason = string.Empty;
        return true;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{X:0.##}, {Y:0.##} on map {UIMapId}");
}
