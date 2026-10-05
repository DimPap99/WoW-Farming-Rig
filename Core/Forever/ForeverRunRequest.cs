using SharedLib.Forever;

using System;

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

    /// <summary>Why the request cannot run as it is, or null.</summary>
    public string? Problem => Mode switch
    {
        ForeverRunMode.Travel when TravelTo is null => "a travel needs its point",
        ForeverRunMode.Roam when Roam is null => "a roam needs its area and kills",
        ForeverRunMode.Route when Route is null => "a route run needs its route",
        _ when Minutes is <= 0 or > MaxMinutes => $"the time bound is above 0 and at most {MaxMinutes} minutes",
        _ => null,
    };
}
