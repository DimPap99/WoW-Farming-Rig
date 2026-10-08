using Contracts.Bots;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using ServiceHosting;

using System.Threading;

namespace BotManager.Bots;

public static class BotEndpoints
{
    public const string Tag = "Bots";

    public static IEndpointRouteBuilder MapBotEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(BotManagerApi.Health, (BotFleet fleet) =>
                new ManagerHealth(BotManagerApi.Version, BotManagerApi.AgentProtocol, fleet.Count, fleet.OnlineCount))
            .WithTags(Tag)
            .WithSummary("The manager's versions, and how many bots it knows and has online")
            .Produces<ManagerHealth>();

        RouteGroupBuilder bots = app.MapGroup(BotManagerApi.Bots).WithTags(Tag);

        bots.MapGet("", (BotFleet fleet) => fleet.List())
            .WithSummary("Every bot: what its agent reported, its assignment, and its latest state while online")
            .Produces<BotInfo[]>();

        bots.MapGet("{agentId}", (string agentId, BotFleet fleet) =>
                fleet.Get(agentId) is { } bot ? Results.Ok(bot) : Results.NotFound())
            .WithSummary("One bot")
            .Produces<BotInfo>()
            .Produces(StatusCodes.Status404NotFound);

        bots.MapDelete("{agentId}", async (string agentId, BotFleet fleet) => (await fleet.ForgetAsync(agentId)).ToHttp())
            .WithSummary("Forget an offline bot and its run history; it comes back if its agent registers again")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        bots.MapPut($"{{agentId}}/{BotManagerApi.Assignment}", (string agentId, BotAssignment assignment, BotFleet fleet) =>
                fleet.Assign(agentId, assignment).ToHttp())
            .WithSummary("Set what the bot runs when it is told to start")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        bots.MapDelete($"{{agentId}}/{BotManagerApi.Assignment}", (string agentId, BotFleet fleet) =>
                fleet.Assign(agentId, null).ToHttp())
            .WithSummary("Clear the bot's assignment")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        bots.MapPost($"{{agentId}}/{BotManagerApi.Start}", async (string agentId, BotFleet fleet, CancellationToken token) =>
                (await fleet.StartAsync(agentId, token)).ToHttp())
            .WithSummary("Start the bot's assignment: the agent's answer, or why it cannot (offline, no assignment, busy)")
            .Produces<CommandAck>()
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        bots.MapPost($"{{agentId}}/{BotManagerApi.Stop}", async (string agentId, BotFleet fleet, CancellationToken token) =>
                (await fleet.StopAsync(agentId, token)).ToHttp())
            .WithSummary("Stop the bot's run: it releases input first, then tears the run down")
            .Produces<CommandAck>()
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        bots.MapGet($"{{agentId}}/{BotManagerApi.Log}", (string agentId, BotFleet fleet) =>
                fleet.Log(agentId) is { } lines ? Results.Ok(lines) : Results.NotFound())
            .WithSummary("The bot's latest log lines, oldest first")
            .Produces<BotLogLine[]>()
            .Produces(StatusCodes.Status404NotFound);

        bots.MapGet($"{{agentId}}/{BotManagerApi.Runs}", (string agentId, BotFleet fleet) =>
                fleet.Runs(agentId) is { } runs ? Results.Ok(runs) : Results.NotFound())
            .WithSummary("The bot's ended runs, newest first")
            .Produces<RunRecord[]>()
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
