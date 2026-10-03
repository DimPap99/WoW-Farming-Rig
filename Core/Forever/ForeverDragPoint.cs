using System;

namespace Core.Forever;

/// <summary>
/// Where a camera drag may begin. A right-button press that lands on a
/// nameplate or a unit targets that unit and attacks it when the button comes
/// up, even after the mouse moved in between (seen live, 2026-10-03), so a
/// person starts the drag on empty ground. Points are normalized like the
/// nameplate markers: X from the left, Y up from the bottom.
/// </summary>
public static class ForeverDragPoint
{
    // In the world view, clear of the action bars, chat, unit frames, quest
    // list and minimap, in order of preference: above the character first.
    private static readonly (double X, double Y)[] Candidates =
    [
        (0.50, 0.67), (0.30, 0.67), (0.70, 0.67), (0.30, 0.50),
        (0.70, 0.50), (0.50, 0.82), (0.30, 0.82), (0.70, 0.82),
    ];

    // A plate sits above its unit: the unit's body covers this much to each
    // side of the marker and this far below it.
    public const double PlateHalfWidth = 0.10;
    public const double BodyBelowPlate = 0.35;
    public const double AbovePlate = 0.05;

    // The character stands in the middle of the view, below this height.
    private const double CharacterHalfWidth = 0.12;
    private const double CharacterTop = 0.62;
    private const double CharacterBottom = 0.10;

    /// <summary>
    /// The first candidate clear of every nameplate in <paramref name="bars"/>,
    /// the body under it, and the character; null when every one is covered.
    /// Units without a plate in the roster, such as friendly players, are not
    /// known and not avoided.
    /// </summary>
    public static (double X, double Y)? Choose(VisualBarSample bars)
    {
        foreach ((double x, double y) in Candidates)
        {
            if (IsClear(bars, x, y))
                return (x, y);
        }
        return null;
    }

    private static bool IsClear(VisualBarSample bars, double x, double y)
    {
        if (Math.Abs(x - 0.5) <= CharacterHalfWidth && y >= CharacterBottom && y <= CharacterTop)
            return false;
        if (bars.TargetScreenPosition is { } target && Covers(target, x, y))
            return false;
        if (bars.Nameplates is { } roster)
        {
            for (int slot = 1; slot <= VisualNameplateRoster.SlotCount; slot++)
            {
                if (roster.GetSlot(slot)?.Screen is { } marker && Covers(marker, x, y))
                    return false;
            }
        }
        return true;
    }

    private static bool Covers(VisualScreenPointSample marker, double x, double y) =>
        Math.Abs(x - marker.X) <= PlateHalfWidth &&
        y <= marker.Y + AbovePlate && y >= marker.Y - BodyBelowPlate;
}
