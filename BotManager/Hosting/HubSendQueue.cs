using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BotManager.Hosting;

/// <summary>
/// Sends on a hub from one loop, in the order the sends were posted, so whoever posts never waits on a
/// socket. A listener that stops reading holds this loop until SignalR drops it, never the caller: an
/// agent's hub call that awaited the fleet's listeners held that agent's connection, the answers to
/// the manager's commands included, as long as one Control Center did not read (known issue 73).
/// </summary>
public sealed partial class HubSendQueue(ILogger<HubSendQueue> logger) : BackgroundService
{
    private readonly Channel<Func<CancellationToken, Task>> sends =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Sends <paramref name="method"/> to <paramref name="clients"/> after what was posted before it.</summary>
    public void Post(IClientProxy clients, string method, params object?[] args)
    {
        sends.Writer.TryWrite(token => clients.SendCoreAsync(method, args, token));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (Func<CancellationToken, Task> send in sends.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await send(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    LogSendFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "A hub send failed")]
    static partial void LogSendFailed(ILogger logger, Exception exception);
}
