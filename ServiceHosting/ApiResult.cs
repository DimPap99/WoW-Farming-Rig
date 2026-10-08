using Microsoft.AspNetCore.Http;

namespace ServiceHosting;

/// <summary>What a service did with a request; one mapping to HTTP (<see cref="ApiResultHttp"/>) for every endpoint of every service.</summary>
public enum ApiOutcome
{
    Done,

    /// <summary>Nothing by that name.</summary>
    NotFound,

    /// <summary>The request was refused; <c>Error</c> says why, for the person who sent it.</summary>
    Invalid,

    /// <summary>The request is sound but clashes with what the service is doing or holds (a bake runs, a bot is offline); <c>Error</c> says what.</summary>
    Conflict,

    /// <summary>The service's own data could not be read or written; <c>Error</c> names it, and nothing changed.</summary>
    Unreadable,

    /// <summary>What the service waited on (an agent) did not answer in time.</summary>
    Timeout,
}

/// <summary>The outcome of an operation that returns nothing.</summary>
public readonly record struct ApiResult(ApiOutcome Outcome, string? Error = null)
{
    public static readonly ApiResult Done = new(ApiOutcome.Done);

    public static readonly ApiResult NotFound = new(ApiOutcome.NotFound);

    public static ApiResult Invalid(string error)
    {
        return new(ApiOutcome.Invalid, error);
    }

    public static ApiResult Conflict(string error)
    {
        return new(ApiOutcome.Conflict, error);
    }

    public static ApiResult Unreadable(string error)
    {
        return new(ApiOutcome.Unreadable, error);
    }
}

/// <summary>The outcome of an operation that returns a <typeparamref name="T"/>.</summary>
public readonly record struct ApiResult<T>(ApiOutcome Outcome, T? Value = default, string? Error = null)
{
    public static readonly ApiResult<T> NotFound = new(ApiOutcome.NotFound);

    public static ApiResult<T> Done(T value)
    {
        return new(ApiOutcome.Done, value);
    }

    public static ApiResult<T> Invalid(string error)
    {
        return new(ApiOutcome.Invalid, default, error);
    }

    public static ApiResult<T> Conflict(string error)
    {
        return new(ApiOutcome.Conflict, default, error);
    }

    public static ApiResult<T> Unreadable(string error)
    {
        return new(ApiOutcome.Unreadable, default, error);
    }

    public static ApiResult<T> Timeout(string error)
    {
        return new(ApiOutcome.Timeout, default, error);
    }
}

/// <summary>One mapping from outcomes to HTTP, so every endpoint of every service answers alike.</summary>
public static class ApiResultHttp
{
    /// <summary>The title of a 400: the request was refused, and the detail says why.</summary>
    public const string RefusedTitle = "Request refused";

    public static IResult ToHttp(this ApiResult result)
    {
        return result.Outcome switch
        {
            ApiOutcome.Done => Results.NoContent(),
            ApiOutcome.NotFound => Results.NotFound(),
            _ => Problem(result.Outcome, result.Error),
        };
    }

    public static IResult ToHttp<T>(this ApiResult<T> result)
    {
        return result.Outcome switch
        {
            ApiOutcome.Done => Results.Ok(result.Value),
            ApiOutcome.NotFound => Results.NotFound(),
            _ => Problem(result.Outcome, result.Error),
        };
    }

    private static IResult Problem(ApiOutcome outcome, string? error)
    {
        return outcome switch
        {
            ApiOutcome.Invalid => Results.Problem(detail: error, statusCode: StatusCodes.Status400BadRequest, title: RefusedTitle),
            ApiOutcome.Conflict => Results.Problem(detail: error, statusCode: StatusCodes.Status409Conflict, title: "Not now"),
            ApiOutcome.Timeout => Results.Problem(detail: error, statusCode: StatusCodes.Status504GatewayTimeout, title: "No answer"),
            _ => Results.Problem(detail: error, statusCode: StatusCodes.Status500InternalServerError, title: "Stored data unreadable"),
        };
    }
}
