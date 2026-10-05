using BotAgent.Runtime;

using Contracts;
using Contracts.Bots;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ServiceClients.Bots;

using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BotAgent.Link;

/// <summary>How the link dials out, and how a test reaches an in-memory manager.</summary>
/// <param name="Backoff">The waits between attempts, the last repeated; each varies by a fifth so agents do not dial in step.</param>
/// <param name="RefusedRetry">How long a manager that refused this agent (another protocol) is left alone.</param>
public sealed record AgentLinkOptions(TimeSpan[] Backoff, TimeSpan RefusedRetry)
{
    public static AgentLinkOptions Default { get; } = new(
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
         TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)],
        TimeSpan.FromMinutes(5));

    /// <summary>For a test server: the handler every request goes through.</summary>
    public Func<HttpMessageHandler>? HttpMessageHandlerFactory { get; init; }

    /// <summary>For a test server, which has no WebSockets: long polling.</summary>
    public HttpTransportType? Transports { get; init; }
}

/// <summary>
/// The agent's line to the Bot Manager. It dials out (nothing ever calls the agent), registers on
/// every connection, answers the manager's commands through the run host, and sends snapshots, log
/// lines and run reports. A manager that is down changes nothing for the bot: the link keeps trying
/// with backoff, the run goes on, and the reports of runs that ended meanwhile are sent once it is
/// back. With no manager configured the agent runs on its own and this does nothing.
/// </summary>
public sealed partial class AgentLink(AgentIdentity identity, AgentOptions options, AgentLinkOptions linkOptions,
    BotRuntimeHost host, IGameProbe probe, IClassProfileCatalog profiles, ILogger<AgentLink> logger) : BackgroundService
{
    /// <summary>Run reports kept while the manager is down.</summary>
    public const int KeptReports = 50;

    private static readonly TimeSpan FinalFlush = TimeSpan.FromSeconds(3);

    private readonly ConcurrentQueue<RunReport> reports = new();
    private readonly SemaphoreSlim flushing = new(1, 1);
    private volatile HubConnection? connection;
    private volatile bool registered;

    /// <summary>Connected and registered: what is sent now reaches the manager.</summary>
    public bool Connected => registered;

    /// <summary>Sends a snapshot; false when the manager is not reachable now.</summary>
    public Task<bool> SendSnapshotAsync(BotSnapshot snapshot, CancellationToken token)
    {
        return SendAsync(AgentHubMethods.Snapshot, snapshot, token);
    }

    /// <summary>Sends log lines; false when the manager is not reachable now, so they are sent later.</summary>
    public Task<bool> SendLogAsync(BotLogLine[] lines, CancellationToken token)
    {
        return SendAsync(AgentHubMethods.Log, lines, token);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // The run host stops first: the report of the run it just stopped goes out before the line does.
        if (registered && connection is { } open)
        {
            try
            {
                await FlushReportsAsync(open, cancellationToken).WaitAsync(FinalFlush, cancellationToken);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        flushing.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        if (options.Manager is not { } manager)
        {
            LogStandalone(logger);
            return;
        }

        HubConnection hub = Build(manager);
        connection = hub;
        host.RunEnded += OnRunEnded;
        try
        {
            await StayConnectedAsync(hub, manager, stopping);
        }
        finally
        {
            registered = false;
            host.RunEnded -= OnRunEnded;
            await hub.DisposeAsync();
        }
    }

    private async Task StayConnectedAsync(HubConnection hub, BotManagerOptions manager, CancellationToken stopping)
    {
        int failures = 0;
        while (!stopping.IsCancellationRequested)
        {
            TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task OnClosed(Exception? _)
            {
                closed.TrySetResult();
                return Task.CompletedTask;
            }

            hub.Closed += OnClosed;
            TimeSpan wait;
            try
            {
                await hub.StartAsync(stopping);
                AgentWelcome welcome = await hub.InvokeAsync<AgentWelcome>(AgentHubMethods.Register, Hello(), stopping);
                if (!welcome.Accepted)
                {
                    LogRefused(logger, manager.Url, welcome.Reason ?? "no reason given");
                    await hub.StopAsync(stopping);
                    wait = linkOptions.RefusedRetry;
                }
                else
                {
                    host.Assignment = welcome.Assignment;
                    registered = true;
                    failures = 0;
                    LogConnected(logger, manager.Url, identity.Name, identity.AgentId);
                    await FlushReportsAsync(hub, stopping);
                    await closed.Task.WaitAsync(stopping);
                    registered = false;
                    LogLost(logger, manager.Url);
                    wait = Backoff(0);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                registered = false;
                if (failures == 0)
                {
                    LogUnreachable(logger, manager.Url, ex.Message);
                }

                wait = Backoff(failures++);
                if (hub.State != HubConnectionState.Disconnected)
                {
                    await hub.StopAsync(CancellationToken.None);
                }
            }
            finally
            {
                hub.Closed -= OnClosed;
            }

            try
            {
                await Task.Delay(wait, stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private HubConnection Build(BotManagerOptions manager)
    {
        HubConnection hub = new HubConnectionBuilder()
            .WithUrl(new Uri(manager.Url, BotManagerApi.AgentHub), http =>
            {
                if (!string.IsNullOrEmpty(manager.ApiKey))
                {
                    http.Headers[ServiceKey.Header] = manager.ApiKey;
                }

                if (linkOptions.HttpMessageHandlerFactory is { } handler)
                {
                    http.HttpMessageHandlerFactory = _ => handler();
                }

                if (linkOptions.Transports is { } transports)
                {
                    http.Transports = transports;
                }
            })
            .AddMessagePackProtocol()
            .Build();

        hub.On<BotCommand, CommandAck>(AgentHubMethods.Command, Obey);
        hub.On<BotAssignment?>(AgentHubMethods.Assign, assignment => host.Assignment = assignment);
        return hub;
    }

    private CommandAck Obey(BotCommand command)
    {
        LogCommand(logger, command.Kind, command.Assignment);
        return command.Kind switch
        {
            BotCommandKind.Start => host.Start(command.Assignment),
            BotCommandKind.Stop => host.Stop(),
            _ => new CommandAck(false, $"this agent does not know the command {command.Kind}", null),
        };
    }

    private AgentHello Hello()
    {
        return new AgentHello(identity.AgentId, identity.Name, identity.Machine, BotManagerApi.AgentProtocol, options.Version,
            probe.Probe(), host.CurrentRun, profiles.List());
    }

    private void OnRunEnded(RunReport report)
    {
        reports.Enqueue(report);
        while (reports.Count > KeptReports && reports.TryDequeue(out _))
        {
        }

        if (registered && connection is { } hub)
        {
            _ = FlushReportsAsync(hub, CancellationToken.None);
        }
    }

    // In order, one at a time; a report leaves the queue only once the manager took it.
    private async Task FlushReportsAsync(HubConnection hub, CancellationToken token)
    {
        await flushing.WaitAsync(token);
        try
        {
            while (reports.TryPeek(out RunReport? report))
            {
                await hub.InvokeAsync(AgentHubMethods.RunEnded, report, token);
                reports.TryDequeue(out _);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            // Kept for the next connection.
        }
        finally
        {
            flushing.Release();
        }
    }

    private async Task<bool> SendAsync(string method, object payload, CancellationToken token)
    {
        if (!registered || connection is not { } hub)
        {
            return false;
        }

        try
        {
            await hub.SendAsync(method, payload, token);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            return false;
        }
    }

    private TimeSpan Backoff(int failures)
    {
        TimeSpan step = linkOptions.Backoff[Math.Min(failures, linkOptions.Backoff.Length - 1)];
        return step * (0.8 + 0.4 * Random.Shared.NextDouble());
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "No Bot Manager configured (BotManager:Url): this agent runs on its own")]
    static partial void LogStandalone(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered with the Bot Manager at {Url} as {Name} ({AgentId})")]
    static partial void LogConnected(ILogger logger, Uri url, string name, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the Bot Manager at {Url}; the bot plays on and the agent dials again")]
    static partial void LogLost(ILogger logger, Uri url);

    [LoggerMessage(Level = LogLevel.Information, Message = "The Bot Manager at {Url} is not reachable ({Reason}); the bot plays on and the agent keeps trying")]
    static partial void LogUnreachable(ILogger logger, Uri url, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Bot Manager at {Url} refused this agent: {Reason}")]
    static partial void LogRefused(ILogger logger, Uri url, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The manager asks: {Kind} {Assignment}")]
    static partial void LogCommand(ILogger logger, BotCommandKind kind, BotAssignment? assignment);

    #endregion
}
