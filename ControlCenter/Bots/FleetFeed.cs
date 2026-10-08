using ControlCenter.Hosting;

using Contracts.Bots;

using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ServiceClients;
using ServiceClients.Bots;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ControlCenter.Bots;

/// <summary>
/// The fleet as every page sees it: one connection to the manager's fleet hub
/// (<see cref="BotManagerApi.FleetHub"/>) for the whole Control Center, kept up forever. The hub
/// replays nothing, so after every connect the bots are read again over REST, and what the hub
/// streams meanwhile is held and applied after the list. It keeps each bot's latest
/// <see cref="BotInfo"/>, so pages read it here instead of each polling, and passes log lines and
/// ended runs on to the consoles. Its events come on SignalR's threads.
/// </summary>
public sealed partial class FleetFeed(IServiceProvider services, IFleetConnectionFactory connections, ILogger<FleetFeed> logger)
    : BackgroundService
{
    /// <summary>The waits between attempts to connect, the last one repeated: the feed never gives up.</summary>
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private readonly Lock sync = new();
    private readonly SemaphoreSlim resyncing = new(1, 1);
    private Dictionary<string, BotInfo> bots = [];
    // While the bots are being read: what the hub streamed meanwhile, applied after the list.
    private List<Action>? held;
    private volatile bool connected;

    /// <summary>Connected to the hub and read since: what <see cref="Bots"/> holds is current.</summary>
    public bool Connected => connected;

    /// <summary>Every bot the manager knows, by name.</summary>
    public IReadOnlyList<BotInfo> Bots
    {
        get
        {
            lock (sync)
            {
                return [.. bots.Values.OrderBy(bot => bot.Name, StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    /// <summary>A bot changed, by its id; null when the whole fleet or the connection did.</summary>
    public event Action<string?>? Changed;

    /// <summary>The bots were read again, after a connect: a console reads its log and runs again too.</summary>
    public event Action? Resynced;

    /// <summary>Lines a bot logged: its id and the lines, oldest first.</summary>
    public event Action<string, BotLogLine[]>? Log;

    /// <summary>A bot's run ended.</summary>
    public event Action<RunRecord>? RunEnded;

    public BotInfo? Get(string agentId)
    {
        lock (sync)
        {
            return bots.GetValueOrDefault(agentId);
        }
    }

    /// <summary>Reads every bot again; what the hub streams meanwhile is applied after the list, in order.</summary>
    public async Task<bool> ResyncAsync(CancellationToken token = default)
    {
        await resyncing.WaitAsync(token);
        try
        {
            Hold();
            ServiceResult<BotInfo[]> listed = default;
            try
            {
                listed = await services.GetRequiredService<IBotManagerClient>().ListBotsAsync(token);
            }
            finally
            {
                Release(listed.Ok ? listed.Value : null);
            }

            Subscribers.Raise(Changed, handler => handler(null), logger);
            if (!listed.Ok)
            {
                LogUnread(logger, listed.Error ?? "no answer");
                return false;
            }

            Subscribers.Raise(Resynced, handler => handler(), logger);
            return true;
        }
        finally
        {
            resyncing.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using HubConnection hub = connections.Create();
        Listen(hub);

        int failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                failures = await StayConnectedAsync(hub, failures, stoppingToken) ? 0 : failures + 1;
                await Task.Delay(Backoff[Math.Min(failures, Backoff.Length - 1)], stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public override void Dispose()
    {
        resyncing.Dispose();
        base.Dispose();
    }

    // Connects, reads the bots and follows the hub until the connection closes: true when it got
    // that far. A failure is logged only when it follows a success, so an outage logs once.
    private async Task<bool> StayConnectedAsync(HubConnection hub, int failures, CancellationToken stopping)
    {
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task OnClosed(Exception? _)
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        }

        hub.Closed += OnClosed;
        try
        {
            // From before the first message can arrive until the list is read.
            Hold();
            try
            {
                await hub.StartAsync(stopping);
            }
            catch (Exception ex) when (!stopping.IsCancellationRequested)
            {
                Release(null);
                if (failures == 0)
                {
                    LogUnreachable(logger, ex.Message);
                }

                return false;
            }

            if (!await ResyncAsync(stopping))
            {
                await hub.StopAsync(CancellationToken.None);
                return false;
            }

            SetConnected(true);
            LogConnected(logger);
            await closed.Task.WaitAsync(stopping);
            LogLost(logger);
            return true;
        }
        finally
        {
            hub.Closed -= OnClosed;
            SetConnected(false);
        }
    }

    private void Listen(HubConnection hub)
    {
        hub.On<BotInfo>(FleetHubMethods.BotChanged, bot => Apply(bot.AgentId, () => bots[bot.AgentId] = bot));
        hub.On<string>(FleetHubMethods.BotRemoved, agentId => Apply(agentId, () => bots.Remove(agentId)));
        hub.On<BotSnapshot>(FleetHubMethods.Snapshot, snapshot => Apply(snapshot.AgentId, () => Update(snapshot)));
        hub.On<string, BotLogLine[]>(FleetHubMethods.Log, (agentId, lines) => Subscribers.Raise(Log, handler => handler(agentId, lines), logger));
        hub.On<RunRecord>(FleetHubMethods.RunEnded, record =>
        {
            // The manager has let the run go: no "run under way" until the next snapshot (known issue 77).
            Apply(record.AgentId, () => Ended(record));
            Subscribers.Raise(RunEnded, handler => handler(record), logger);
        });
    }

    // A change from the hub: applied now, or held while the bots are being read.
    private void Apply(string agentId, Action change)
    {
        lock (sync)
        {
            if (held is not null)
            {
                held.Add(change);
                return;
            }

            change();
        }

        Subscribers.Raise(Changed, handler => handler(agentId), logger);
    }

    // Under the lock: the bot's run, if it is the one that ended.
    private void Ended(RunRecord record)
    {
        if (bots.TryGetValue(record.AgentId, out BotInfo? bot) && bot.Run?.RunId == record.Report.Run.RunId)
        {
            bots[record.AgentId] = bot with { Run = null };
        }
    }

    // Under the lock. A snapshot carries the bot's run and client too, as the manager's own BotInfo does.
    private void Update(BotSnapshot snapshot)
    {
        if (bots.TryGetValue(snapshot.AgentId, out BotInfo? bot))
        {
            bots[snapshot.AgentId] = bot with { Latest = snapshot, Run = snapshot.Run, Game = snapshot.Game ?? bot.Game };
        }
    }

    private void Hold()
    {
        lock (sync)
        {
            held ??= [];
        }
    }

    // The list read, if it was, and then what was held, in the order it came.
    private void Release(BotInfo[]? listed)
    {
        lock (sync)
        {
            if (listed is not null)
            {
                bots = listed.ToDictionary(bot => bot.AgentId);
            }

            foreach (Action change in held ?? [])
            {
                change();
            }

            held = null;
        }
    }

    private void SetConnected(bool value)
    {
        if (connected != value)
        {
            connected = value;
            Subscribers.Raise(Changed, handler => handler(null), logger);
        }
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Fleet feed connected")]
    static partial void LogConnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fleet feed lost its connection; reconnecting")]
    static partial void LogLost(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fleet feed cannot connect ({Reason}); it keeps trying")]
    static partial void LogUnreachable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fleet feed could not read the bots: {Reason}")]
    static partial void LogUnread(ILogger logger, string reason);

    #endregion
}
