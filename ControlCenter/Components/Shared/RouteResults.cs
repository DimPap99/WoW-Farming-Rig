using Contracts.Pathing;

using System.Globalization;

namespace ControlCenter.Components.Shared;

/// <summary>How a route answer reads in a result line.</summary>
public static class RouteResults
{
    public static (string Text, ResultKind Kind) Describe(RouteResponse response)
    {
        string ms = response.ElapsedMs.ToString("F0", CultureInfo.InvariantCulture);
        return response.Status switch
        {
            RouteStatus.Found => ($"{response.Points.Length} points in {ms} ms", ResultKind.Ok),
            RouteStatus.NoPath => ($"No path ({ms} ms): {response.Detail}", ResultKind.Warn),
            RouteStatus.NotBaked => ($"Not baked: {response.Detail}", ResultKind.Warn),
            RouteStatus.BadRequest => ($"Refused: {response.Detail}", ResultKind.Error),
            _ => ($"Unavailable: {response.Detail}", ResultKind.Error),
        };
    }

    /// <summary>The short form a list row has room for.</summary>
    public static (string Text, ResultKind Kind) Brief(RouteResponse response)
    {
        string ms = response.ElapsedMs.ToString("F0", CultureInfo.InvariantCulture);
        return response.Status switch
        {
            RouteStatus.Found => ($"{response.Points.Length} pts · {ms} ms", ResultKind.Ok),
            RouteStatus.NoPath => ("no path", ResultKind.Warn),
            RouteStatus.NotBaked => ("not baked", ResultKind.Warn),
            RouteStatus.BadRequest => ("refused", ResultKind.Error),
            _ => ("offline", ResultKind.Error),
        };
    }
}
