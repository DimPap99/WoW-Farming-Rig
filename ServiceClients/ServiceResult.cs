using System.Diagnostics.CodeAnalysis;

namespace ServiceClients;

/// <summary>
/// How a call to a service went, without an exception: callers are UIs and bots that show or
/// log <see cref="Error"/> and carry on.
/// </summary>
/// <param name="Status">The HTTP status, or 0 when the service could not be reached.</param>
/// <param name="Error">Why it failed, for a person to read; null on success.</param>
public readonly record struct ServiceResult(int Status, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>A <see cref="ServiceResult"/> that carries a value on success.</summary>
public readonly record struct ServiceResult<T>(T? Value, int Status, string? Error)
{
    [MemberNotNullWhen(true, nameof(Value))]
    public bool Ok => Error is null && Value is not null;
}
