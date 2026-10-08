using SharedLib.Forever;

using System;
using System.Linq;

namespace Core.Forever;

/// <summary>What a <see cref="ForeverRun"/> does: the trial runner's options, one mode each.</summary>
public enum ForeverRunMode
{
    /// <summary>The Paladin route trial's read-only check: no option, or <c>--check</c>.</summary>
    Check,
    /// <summary>The bounded Paladin route trial: <c>--run</c>.</summary>
    Run,
    /// <summary>The autonomous preflight and the pulls it would weigh, read-only: <c>--auto-check</c>.</summary>
    AutoCheck,
    /// <summary>One autonomous kill, loot and recovery cycle: <c>--auto-run</c>.</summary>
    AutoRun,
    /// <summary>Cycles until a full look-around finds nothing to take: <c>--sweep</c>.</summary>
    Sweep,
    /// <summary>Walk to a point of the current zone's map: <c>--travel</c>.</summary>
    Travel,
    /// <summary>Roam a rectangle of the current zone's map until enough kills: <c>--roam</c>.</summary>
    Roam,
    /// <summary>A route file's steps in order: <c>--route</c>.</summary>
    Route,
}

/// <summary>A rectangle of the current zone's map by opposite corners, 0 to 100, roamed until <paramref name="Kills"/> kills.</summary>
public readonly record struct ForeverRoamRequest(double X1, double Y1, double X2, double Y2, int Kills);

/// <summary>
/// A run of the Forever controller, as the trial runner's command line or an agent's assignment
/// asks for it. Each mode keeps the time bound the trial runner has always given it; an
/// autonomous run may set its own (<see cref="Minutes"/>).
/// </summary>
public sealed record ForeverRunRequest(ForeverRunMode Mode)
{
    /// <summary>The route trial's bound; <see cref="Minutes"/> does not change it.</summary>
    public const int TrialSeconds = 195;
    public const int AutoRunSeconds = 170;
    public const int TravelSeconds = 300;
    public const int SweepSeconds = 600;
    public const int RoamSeconds = 900;
    public const int RouteSeconds = 1200;

    /// <summary>The longest time bound a run may ask for.</summary>
    public const double MaxMinutes = 600;

    /// <summary>The client whose data a run reads, under <see cref="DataRoot"/>.</summary>
    public const string Client = "forever";

    /// <summary>For <see cref="ForeverRunMode.Travel"/>: the point of the current zone's map, 0 to 100.</summary>
    public (double X, double Y)? TravelTo { get; init; }

    /// <summary>For <see cref="ForeverRunMode.Roam"/>.</summary>
    public ForeverRoamRequest? Roam { get; init; }

    /// <summary>For <see cref="ForeverRunMode.Route"/>: the route, already loaded and checked.</summary>
    public ForeverRoute? Route { get; init; }

    /// <summary>The autonomous run's time bound in minutes, instead of its mode's own.</summary>
    public double? Minutes { get; init; }

    /// <summary>The data folder (<c>Json</c>) the run reads its profiles, maps and navmesh from.</summary>
    public required string DataRoot { get; init; }

    /// <summary>Where a capture the bars cannot be read from is kept; null keeps none.</summary>
    public string? UnreadableFrameFolder { get; init; }

    /// <summary>The autonomous controller rather than the Paladin route trial.</summary>
    public bool IsAutonomous => Mode is not (ForeverRunMode.Check or ForeverRunMode.Run);

    /// <summary>The run sends input; the check modes only read.</summary>
    public bool Moves => Mode is not (ForeverRunMode.Check or ForeverRunMode.AutoCheck);

    /// <summary>How long a run that moves may go on once it started.</summary>
    public double TimeBoundSeconds => Mode switch
    {
        ForeverRunMode.Check or ForeverRunMode.Run => TrialSeconds,
        _ when Minutes is double minutes => minutes * 60,
        ForeverRunMode.Route => RouteSeconds,
        ForeverRunMode.Roam => RoamSeconds,
        ForeverRunMode.Sweep => SweepSeconds,
        ForeverRunMode.Travel => TravelSeconds,
        _ => AutoRunSeconds,
    };

    /// <summary>
    /// Why the request cannot run as it is, or null: what its mode needs and nothing of another mode's,
    /// points on the map, a roam that has kills to make, and a time bound only where it counts. The trial
    /// runner's command line and an agent's assignment are refused alike, before anything attaches.
    /// </summary>
    public string? Problem => FindProblem();

    private string? FindProblem()
    {
        string? missing = Mode switch
        {
            ForeverRunMode.Travel when TravelTo is null => "a travel needs its point",
            ForeverRunMode.Roam when Roam is null => "a roam needs its area and kills",
            ForeverRunMode.Route when Route is null => "a route run needs its route",
            _ => null,
        };
        if (missing is not null)
        {
            return missing;
        }

        // A travel given a route ran the route, under the travel's bound.
        if (TravelTo is not null && Mode != ForeverRunMode.Travel)
        {
            return $"a point to travel to is for a travel, not a {Mode} run";
        }

        if (Roam is not null && Mode != ForeverRunMode.Roam)
        {
            return $"an area to roam is for a roam, not a {Mode} run";
        }

        if (Route is not null && Mode != ForeverRunMode.Route)
        {
            return $"a route is for a route run, not a {Mode} run";
        }

        if (TravelTo is { } point && !(ForeverMapPoint.OnMap(point.X) && ForeverMapPoint.OnMap(point.Y)))
        {
            return $"the point to travel to is off the map: its coordinates are 0 to {ForeverMapPoint.MapSize}";
        }

        if (Roam is { } area && !new[] { area.X1, area.Y1, area.X2, area.Y2 }.All(ForeverMapPoint.OnMap))
        {
            return $"the area to roam is off the map: its corners are 0 to {ForeverMapPoint.MapSize}";
        }

        // With none to make, it ended at once.
        if (Roam is { Kills: < 1 })
        {
            return "a roam needs at least one kill to make";
        }

        if (Minutes is not null && !(IsAutonomous && Moves))
        {
            return $"a time bound is for an autonomous run that moves; a {Mode} run keeps its own";
        }

        if (Minutes is { } bound && !(double.IsFinite(bound) && bound is > 0 and <= MaxMinutes))
        {
            return $"the time bound is above 0 and at most {MaxMinutes} minutes";
        }

        return null;
    }
}
