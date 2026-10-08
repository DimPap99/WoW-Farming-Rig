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

    /// <summary>
    /// The contract's conventions, indented: for the state files the services and the agent keep (the manager's
    /// registry, the agent's identity and its sync state), which used three. Names are read in any case.
    /// </summary>
    public static JsonSerializerOptions Files { get; } = CreateFiles();

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

    private static JsonSerializerOptions CreateFiles()
    {
        JsonSerializerOptions options = new(Options) { WriteIndented = true };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
