using System;

namespace Contracts.Pathing;

/// <summary>A WoW: Forever route file in the library, and what a run makes of it.</summary>
/// <param name="Name">The file name in the route folder, ending in <c>.json</c>.</param>
/// <param name="Title">The route's own name, or the file name without its extension when it gives none.</param>
/// <param name="Steps">Each step as a run's log names it, in order; empty when the route is refused.</param>
/// <param name="Repeat">The route starts over after its last step, until the run's time bound.</param>
/// <param name="Problem">Why a run would refuse the route, naming the step; null when it loads.</param>
/// <param name="Bytes">File size; 0 for a draft that is not saved.</param>
/// <param name="ModifiedUtc">Last write time; null for a draft that is not saved.</param>
public sealed record ForeverRouteInfo(string Name, string Title, string[] Steps, bool Repeat, string? Problem,
    long Bytes, DateTimeOffset? ModifiedUtc);

/// <summary>A route file's text, comments and all, with what a run makes of it.</summary>
public sealed record ForeverRouteDocument(string Name, string Text, ForeverRouteInfo Info);

/// <summary>The text to save as a route file. It is checked as a run would load it, then written as given.</summary>
public sealed record ForeverRouteText(string Text);

/// <summary>The text of the route file <paramref name="Name"/>, to check without saving it.</summary>
public sealed record ForeverRouteDraft(string Name, string Text);
