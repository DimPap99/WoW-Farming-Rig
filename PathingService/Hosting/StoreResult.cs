using Microsoft.AspNetCore.Http;

namespace PathingService.Hosting;

/// <summary>What a store did with a request.</summary>
public enum StoreOutcome
{
    Done,

    /// <summary>Nothing by that name.</summary>
    NotFound,

    /// <summary>The request was refused; <c>Error</c> says why, for the person who sent it.</summary>
    Invalid,

    /// <summary>A file the store owns could not be read; <c>Error</c> names it.</summary>
    Unreadable,

    /// <summary>The request is sound but clashes with what the service is doing (a bake runs); <c>Error</c> says what.</summary>
    Conflict
}

/// <summary>The outcome of a store operation that returns nothing.</summary>
public readonly record struct StoreResult(StoreOutcome Outcome, string? Error = null)
{
    public static readonly StoreResult Done = new(StoreOutcome.Done);

    public static readonly StoreResult NotFound = new(StoreOutcome.NotFound);

    public static StoreResult Invalid(string error)
    {
        return new(StoreOutcome.Invalid, error);
    }

    public static StoreResult Conflict(string error)
    {
        return new(StoreOutcome.Conflict, error);
    }

    public static StoreResult Unreadable(string error)
    {
        return new(StoreOutcome.Unreadable, error);
    }
}

/// <summary>The outcome of a store operation that returns a <typeparamref name="T"/>.</summary>
public readonly record struct StoreResult<T>(StoreOutcome Outcome, T? Value = default, string? Error = null)
{
    public static readonly StoreResult<T> NotFound = new(StoreOutcome.NotFound);

    public static StoreResult<T> Done(T value)
    {
        return new(StoreOutcome.Done, value);
    }

    public static StoreResult<T> Invalid(string error)
    {
        return new(StoreOutcome.Invalid, default, error);
    }

    public static StoreResult<T> Unreadable(string error)
    {
        return new(StoreOutcome.Unreadable, default, error);
    }

    public static StoreResult<T> Conflict(string error)
    {
        return new(StoreOutcome.Conflict, default, error);
    }
}

/// <summary>One mapping from store outcomes to HTTP, so every endpoint answers alike.</summary>
public static class StoreResultHttp
{
    /// <summary>The title of a 400: the request was refused, and the detail says why.</summary>
    public const string RefusedTitle = "Request refused";

    public static IResult ToHttp(this StoreResult result)
    {
        return result.Outcome switch
        {
            StoreOutcome.Done => Results.NoContent(),
            StoreOutcome.NotFound => Results.NotFound(),
            _ => Problem(result.Outcome, result.Error),
        };
    }

    public static IResult ToHttp<T>(this StoreResult<T> result)
    {
        return result.Outcome switch
        {
            StoreOutcome.Done => Results.Ok(result.Value),
            StoreOutcome.NotFound => Results.NotFound(),
            _ => Problem(result.Outcome, result.Error),
        };
    }

    private static IResult Problem(StoreOutcome outcome, string? error)
    {
        return outcome switch
        {
            StoreOutcome.Invalid => Results.Problem(detail: error, statusCode: StatusCodes.Status400BadRequest, title: RefusedTitle),
            StoreOutcome.Conflict => Results.Problem(detail: error, statusCode: StatusCodes.Status409Conflict, title: "Not now"),
            _ => Results.Problem(detail: error, statusCode: StatusCodes.Status500InternalServerError, title: "Stored data unreadable"),
        };
    }
}
