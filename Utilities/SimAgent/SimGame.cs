using BotAgent.Runtime;

using Contracts.Bots;

using System;

namespace SimAgent;

/// <summary>The PC as the agent sees it: a made-up client always running (<see cref="ProbeGame"/>) and a few class profiles (<see cref="ListClassProfiles"/>).</summary>
public sealed class SimGame(SimOptions options)
{
    public AgentGame? Probe()
    {
        return options.Forever
            ? new AgentGame(SimOptions.ForeverClient, "forever", Environment.ProcessId, "1.60.1.70009 (simulated)")
            : new AgentGame(options.Client, "precata", Environment.ProcessId, "1.15.7.61582 (simulated)");
    }

    public static string[] List()
    {
        return ["Hunter_10.json", "Warrior_1.json", "Forever/Paladin_Forever_Auto.json"];
    }
}
