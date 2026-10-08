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

    /// <summary>The longest grace a setting may give: a stop, and the host's own, must end in bounded time.</summary>
    public const int MaxStopGraceMs = 60000;

    public static AgentOptions From(IConfiguration configuration)
    {
        string identity = configuration[IdentityFileKey] is { Length: > 0 } file ? file : DefaultIdentityFile;
        int grace = configuration.GetValue(StopGraceMsKey, StopGraceMs);
        return new AgentOptions(identity, configuration[NameKey], BotManagerOptions.FromIfConfigured(configuration),
            TimeSpan.FromMilliseconds(grace > 0 ? Math.Min(grace, MaxStopGraceMs) : StopGraceMs), BuildVersion());
    }

    /// <summary>
    /// How long a host may take to stop: a run's grace, its forced release and its abandon (half the grace
    /// again), then the link's last flush of reports, and a margin. A fixed 30 s ran out from about 19 s of
    /// grace, losing the abandon's report and its restart, and from 30 s the forced release (known issue 76).
    /// </summary>
    public TimeSpan ShutdownTimeout()
    {
        return StopGrace * 1.5 + Runtime.BotRuntimeHost.ReleaseWait + Link.AgentLink.FinalFlush + TimeSpan.FromSeconds(5);
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
