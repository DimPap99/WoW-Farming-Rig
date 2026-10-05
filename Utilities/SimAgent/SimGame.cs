using BotAgent.Runtime;

using Contracts.Bots;

using System;

namespace SimAgent;

/// <summary>The PC as the agent sees it: a made-up client always running, a few class profiles, and a restart that is an exit.</summary>
public sealed class SimGame(SimOptions options) : IGameProbe, IClassProfileCatalog, IAgentProcess
{
    /// <summary>The exit code that asks a launcher to start the agent again (HeadlessServer's AgentProcess).</summary>
    public const int RestartExitCode = 75;

    public AgentGame? Probe()
    {
        return options.Forever
            ? new AgentGame(SimOptions.ForeverClient, "forever", Environment.ProcessId, "1.60.1.70009 (simulated)")
            : new AgentGame(options.Client, "precata", Environment.ProcessId, "1.15.7.61582 (simulated)");
    }

    public string[] List()
    {
        return ["Hunter_10.json", "Warrior_1.json", "Forever/Paladin_Forever_Auto.json"];
    }

    public void Restart(string reason)
    {
        Console.Error.WriteLine($"Restart asked: {reason}");
        Environment.Exit(RestartExitCode);
    }
}
