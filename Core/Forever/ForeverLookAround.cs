using System;

namespace Core.Forever;

/// <summary>
/// Whether the facing has turned through a full circle since the last reset,
/// as a person looks all the way round before deciding nothing is there.
/// </summary>
public sealed class ForeverLookAround
{
    private double turned;
    private double? facing;

    /// <summary>Adds this frame's turn; true once a full circle has passed.</summary>
    public bool Turned(VisualBarSample bars)
    {
        double direction = bars.Position!.Value.Direction;
        if (facing is { } previous)
            turned += Math.Abs(Math.Atan2(Math.Sin(direction - previous), Math.Cos(direction - previous)));
        facing = direction;
        return turned >= 2 * Math.PI;
    }

    public void Reset()
    {
        turned = 0;
        facing = null;
    }
}
