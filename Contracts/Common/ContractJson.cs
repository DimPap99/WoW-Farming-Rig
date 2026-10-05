using System.Text.Json;
using System.Text.Json.Serialization;

namespace Contracts;

/// <summary>
/// The one JSON shape every service and client speaks: ASP.NET Core's web defaults
/// (camelCase, case-insensitive reads) with enums written as their names. A server applies
/// it to the options it already owns, and a client uses <see cref="Options"/>, so the two
/// ends cannot drift apart.
/// </summary>
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Adds the contract's conventions to options a host already owns.</summary>
    public static JsonSerializerOptions Apply(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = Apply(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
