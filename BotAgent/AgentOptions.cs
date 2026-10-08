using Contracts.Bots;

using Microsoft.Extensions.Configuration;

using ServiceClients.Bots;

using System;
using System.Reflection;

namespace BotAgent;

/// <summary>
/// How the agent is set up, from the host's configuration (<c>Agent:*</c>, and <c>BotManager:Url</c>
/// for the manager it dials out to). With no manager configured it runs on its own.
/// </summary>
/// <param name="IdentityFile">Where the agent's id is kept.</param>
/// <param name="Name">What the manager calls it; the machine name when null.</param>
/// <param name="Manager">The manager to dial out to; null runs standalone.</param>
/// <param name="StopGrace">How long a stopped run gets to release input and end before it is forced.</param>
/// <param name="Version">This build, as the agent reports it.</param>
public sealed record AgentOptions(string IdentityFile, string? Name, BotManagerOptions? Manager, TimeSpan StopGrace, string Version)
{
    public const string Position = "Agent";
    public const string IdentityFileKey = Position + ":IdentityFile";
    public const string NameKey = Position + ":Name";
    public const string StopGraceMsKey = Position + ":StopGraceMs";

    public const string DefaultIdentityFile = BotManagerApi.AgentIdentityFile;

    /// <summary>A stopped run's time to release what it holds and end: the trial runner's host gave it the same.</summary>
    public const int StopGraceMs = 10000;

    public static AgentOptions From(IConfiguration configuration)
    {
        string identity = configuration[IdentityFileKey] is { Length: > 0 } file ? file : DefaultIdentityFile;
        int grace = configuration.GetValue(StopGraceMsKey, StopGraceMs);
        return new AgentOptions(identity, configuration[NameKey], BotManagerOptions.FromIfConfigured(configuration),
            TimeSpan.FromMilliseconds(grace > 0 ? grace : StopGraceMs), BuildVersion());
    }

    /// <summary>The address only: the key never reaches a log.</summary>
    public override string ToString()
    {
        return $"{Name ?? "(machine name)"} -> {Manager?.Url.ToString() ?? "standalone"}";
    }

    private static string BuildVersion()
    {
        Assembly entry = Assembly.GetEntryAssembly() ?? typeof(AgentOptions).Assembly;
        return entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? entry.GetName().Version?.ToString() ?? "unknown";
    }
}
