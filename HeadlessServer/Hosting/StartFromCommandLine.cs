using BotAgent.Runtime;

using Contracts.Bots;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeadlessServer.Hosting;

/// <summary>
/// A class profile on the command line runs at once, as HeadlessServer always ran it. Without
/// <c>--agent</c> the process ends with the run; with it, the agent stays for the next assignment.
/// </summary>
public sealed partial class StartFromCommandLine(RunOptions options, BotRuntimeHost host, IHostApplicationLifetime lifetime,
    ILogger<StartFromCommandLine> logger) : IHostedService
{
    public const int ExitRefused = 1;

    private string? runId;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A Windows path in a profile name is a folder separator here.
        BotAssignment assignment = BotAssignment.ForClassic(options.ClassConfig!.Replace('\\', '/'));
        if (!options.Agent)
        {
            host.RunEnded += Ended;
        }

        CommandAck started = host.Start(assignment);
        if (!started.Accepted)
        {
            LogRefused(logger, assignment.ToString(), started.Reason ?? string.Empty);
            if (!options.Agent)
            {
                Environment.ExitCode = ExitRefused;
                lifetime.StopApplication();
            }
            return Task.CompletedTask;
        }

        runId = started.RunId;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        host.RunEnded -= Ended;
        return Task.CompletedTask;
    }

    private void Ended(RunReport report)
    {
        if (report.Run.RunId != runId)
        {
            return;
        }

        if (report.Outcome is not (BotRunOutcome.Complete or BotRunOutcome.Stopped))
        {
            Environment.ExitCode = ExitRefused;
        }

        lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Run} cannot start: {Reason}")]
    static partial void LogRefused(ILogger logger, string run, string reason);
}
