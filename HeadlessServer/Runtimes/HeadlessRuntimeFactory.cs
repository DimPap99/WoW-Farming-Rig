using BotAgent.Runtime;

using Contracts.Bots;

using Core.Forever;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HeadlessServer.Runtimes;

/// <summary>
/// The runtime an assignment needs, for the client running on this PC: a Forever route through
/// Core's <see cref="ForeverRun"/>, or a Classic profile on the bot HeadlessServer always ran. What
/// can be checked before attaching is checked here, so a refusal never touched the game.
/// </summary>
public sealed class HeadlessRuntimeFactory(RunOptions options, IConfiguration configuration, IGameProbe probe,
    ILoggerFactory loggerFactory) : IBotRuntimeFactory
{
    public bool TryCreate(BotAssignment assignment, out IBotRuntime runtime, out string refusal)
    {
        runtime = null!;
        if (probe.Probe() is not { } game)
        {
            refusal = "no WoW client is running on this PC";
            return false;
        }

        bool forever = game.Client == ForeverRunRequest.Client;
        switch (assignment)
        {
            case { Kind: BotRuntimeKind.Forever, Forever: { } route } when forever:
                return ForeverRuntime.TryCreate(route, game, DataConfig.Load(game.Client), loggerFactory, out runtime, out refusal);
            case { Kind: BotRuntimeKind.Forever }:
                refusal = $"a Forever run needs WoW: Forever, and the client running is {game.Client} {game.Version}";
                return false;
            case { Kind: BotRuntimeKind.Classic, Classic: { } profile } when !forever:
                return ClassicRuntime.TryCreate(profile, game, DataConfig.Load(game.Client), options, configuration, loggerFactory,
                    out runtime, out refusal);
            case { Kind: BotRuntimeKind.Classic }:
                refusal = "a Classic run needs a Classic client, and WoW: Forever is running: Forever runs through a route assignment";
                return false;
            default:
                refusal = $"'{assignment.Kind}' is not a kind of run";
                return false;
        }
    }
}
