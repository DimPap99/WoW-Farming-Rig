using Microsoft.AspNetCore.Http;

namespace BotManager.Bots;

/// <summary>What the fleet did with a request.</summary>
public enum FleetOutcome
{
    Done,

    /// <summary>No bot by that id.</summary>
    NotFound,

    /// <summary>The request is malformed; <c>Error</c> says why, for the person who sent it.</summary>
    Invalid,

    /// <summary>The request is sound but the bot cannot take it now (offline, busy, refused); <c>Error</c> says why.</summary>
    Conflict,

    /// <summary>The agent did not answer in time.</summary>
    Timeout,

    /// <summary>The manager could not do it: its data could not be written. <c>Error</c> says why; nothing changed.</summary>
    Failed,
}

/// <summary>The outcome of a fleet operation that returns nothing.</summary>
public readonly record struct FleetResult(FleetOutcome Outcome, string? Error = null)
{
    public static readonly FleetResult Done = new(FleetOutcome.Done);

    public static readonly FleetResult NotFound = new(FleetOutcome.NotFound);

    public static FleetResult Invalid(string error)
    {
        return new(FleetOutcome.Invalid, error);
    }

    public static FleetResult Conflict(string error)
    {
        return new(FleetOutcome.Conflict, error);
    }

    public static FleetResult Failed(string error)
    {
        return new(FleetOutcome.Failed, error);
    }
}

/// <summary>The outcome of a fleet operation that returns a <typeparamref name="T"/>.</summary>
public readonly record struct FleetResult<T>(FleetOutcome Outcome, T? Value = default, string? Error = null)
{
    public static readonly FleetResult<T> NotFound = new(FleetOutcome.NotFound);

    public static FleetResult<T> Done(T value)
    {
        return new(FleetOutcome.Done, value);
    }

    public static FleetResult<T> Conflict(string error)
    {
        return new(FleetOutcome.Conflict, default, error);
    }

    public static FleetResult<T> Timeout(string error)
    {
        return new(FleetOutcome.Timeout, default, error);
    }
}

/// <summary>One mapping from fleet outcomes to HTTP, so every endpoint answers alike.</summary>
public static class FleetResultHttp
{
    public static IResult ToHttp(this FleetResult result)
    {
        return result.Outcome switch
        {
            FleetOutcome.Done => Results.NoContent(),
            FleetOutcome.NotFound => Results.NotFound(),
            _ => Problem(result.Outcome, result.Error),
        };
    }

    public static IResult ToHttp<T>(this FleetResult<T> result)
    {
        return result.Outcome switch
        {
            FleetOutcome.Done => Results.Ok(result.Value),
            FleetOutcome.NotFound => Results.NotFound(),
            _ => Problem(result.Outcome, result.Error),
        };
    }

    private static IResult Problem(FleetOutcome outcome, string? error)
    {
        return outcome switch
        {
            FleetOutcome.Invalid => Results.Problem(detail: error, statusCode: StatusCodes.Status400BadRequest, title: "Request refused"),
            FleetOutcome.Timeout => Results.Problem(detail: error, statusCode: StatusCodes.Status504GatewayTimeout, title: "No answer"),
            FleetOutcome.Failed => Results.Problem(detail: error, statusCode: StatusCodes.Status500InternalServerError, title: "Not done"),
            _ => Results.Problem(detail: error, statusCode: StatusCodes.Status409Conflict, title: "Not now"),
        };
    }
}
