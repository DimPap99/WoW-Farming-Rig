using BotAgent.Runtime;

using Contracts.Bots;

using Core.Forever;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HeadlessServer.Runtimes;

/// <summary>
/// The runtime an assignment needs (the agent's <see cref="CreateRuntime"/>), for the client running
/// on this PC: a Forever route through Core's <see cref="ForeverRun"/>, or a Classic profile on the
/// bot HeadlessServer always ran. What can be checked before attaching is checked here, so a refusal
/// never touched the game.
/// </summary>
public sealed class HeadlessRuntimeFactory(RunOptions options, IConfiguration configuration, ProbeGame probe,
    ILoggerFactory loggerFactory)
{
    public (IBotRuntime? Runtime, string? Refusal) Create(BotAssignment assignment)
    {
        if (probe() is not { } game)
        {
            return (null, "no WoW client is running on this PC");
        }

        bool forever = game.Client == ForeverRunRequest.Client;
        return assignment switch
        {
            { Kind: BotRuntimeKind.Forever, Forever: { } route } when forever =>
                ForeverRuntime.Create(route, game, DataConfig.Load(game.Client), loggerFactory),
            { Kind: BotRuntimeKind.Forever } =>
                (null, $"a Forever run needs WoW: Forever, and the client running is {game.Client} {game.Version}"),
            { Kind: BotRuntimeKind.Classic, Classic: { } profile } when !forever =>
                ClassicRuntime.Create(profile, game, DataConfig.Load(game.Client), options, configuration, loggerFactory),
            { Kind: BotRuntimeKind.Classic } =>
                (null, "a Classic run needs a Classic client, and WoW: Forever is running: Forever runs through a route assignment"),
            _ => (null, $"'{assignment.Kind}' is not a kind of run"),
        };
    }
}
