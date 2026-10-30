using System;

namespace Contracts.Pathing;

/// <summary>
/// The methods of the watch hub (<see cref="PathingApi.WatchHub"/>), which streams the routes the
/// planner answers to route watchers such as the Control Center. The hub speaks MessagePack.
/// </summary>
public static class WatchHubMethods
{
    /// <summary>To route watchers: forget the routes shown so far (<see cref="PathingApi.WatchClear"/>).</summary>
    public const string Clear = "clear";

    /// <summary>
    /// From a route watcher, such as the Control Center: from then on the connection gets each route
    /// the planner answers as <see cref="RoutePlanned"/>, the remembered ones first, and
    /// <see cref="Clear"/>.
    /// </summary>
    public const string WatchRoutes = "WatchRoutes";

    /// <summary>To route watchers (<see cref="WatchRoutes"/>): <c>(WatchRoute)</c>, a route the planner answered.</summary>
    public const string RoutePlanned = "routePlanned";
}

/// <summary>A route the planner answered, for any caller, as a route watcher gets it; positions in world yards.</summary>
/// <param name="Continent">The continent it is on, as the planner names it.</param>
/// <param name="From">Where it starts, as the planner resolved it (with the ground's height).</param>
/// <param name="To">Where it was going.</param>
/// <param name="Points">The route as far as it goes; empty when there is none.</param>
/// <param name="At">When the planner answered it.</param>
public sealed record WatchRoute(string Continent, Point3 From, Point3 To, Point3[] Points, RouteStatus Status,
    double ElapsedMs, string? Detail, DateTimeOffset At);
