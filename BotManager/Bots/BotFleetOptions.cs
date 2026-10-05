using Microsoft.Extensions.Configuration;

using System;

namespace BotManager.Bots;

/// <summary>How the fleet waits on agents.</summary>
/// <param name="CommandTimeout">How long a command waits for the agent's answer.</param>
public sealed record BotFleetOptions(TimeSpan CommandTimeout)
{
    /// <summary>Seconds a command waits for the agent's answer.</summary>
    public const string CommandTimeoutKey = "CommandTimeoutSeconds";

    public const int DefaultCommandTimeoutSeconds = 10;

    public static BotFleetOptions From(IConfiguration configuration)
    {
        double seconds = configuration.GetValue(CommandTimeoutKey, (double)DefaultCommandTimeoutSeconds);
        return new BotFleetOptions(TimeSpan.FromSeconds(seconds > 0 ? seconds : DefaultCommandTimeoutSeconds));
    }
}
