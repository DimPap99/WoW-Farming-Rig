using BotAgent;

using HeadlessServer;
using HeadlessServer.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using System;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace AgentTests.Agent;

/// <summary>
/// What follows the end of HeadlessServer's host: an agent's is built again after a failure, the
/// process ends after a stop, and a run that could not be torn down asks for a new process.
/// </summary>
public sealed class ResidentHostTests
{
    [Theory]
    [InlineData(false, false, false, false, false, HostEnd.Stopped)]
    [InlineData(false, true, true, false, false, HostEnd.StoppedOnError)]
    [InlineData(false, true, false, false, false, HostEnd.Rebuild)]
    [InlineData(false, false, true, true, false, HostEnd.Rebuild)]
    [InlineData(false, true, true, true, false, HostEnd.Rebuild)]
    [InlineData(false, false, true, true, true, HostEnd.Restart)]
    [InlineData(true, false, true, false, false, HostEnd.Restart)]
    public void What_follows_a_hosts_end(bool restartRequested, bool threw, bool stopRequested, bool serviceFaulted, bool runUnderWay,
        HostEnd end)
    {
        Assert.Equal(end, ResidentHost.Decide(restartRequested, threw, stopRequested, serviceFaulted, runUnderWay));
    }

    [Fact]
    public void A_host_whose_service_faults_is_built_again_though_dotnet_reports_a_clean_stop()
    {
        int built = 0;

        int code = ResidentHost.Run(() => ++built == 1 ? Host<Faulting>() : Host<Stopping>(), NullLogger.Instance, resident: true,
            TimeSpan.Zero, () => 0);

        Assert.Equal(2, built);
        Assert.Equal(0, code);
    }

    [Fact]
    public void A_host_that_cannot_be_built_is_built_again()
    {
        int built = 0;

        int code = ResidentHost.Run(() => ++built == 1 ? throw new InvalidOperationException("a setting is wrong") : Host<Stopping>(),
            NullLogger.Instance, resident: true, TimeSpan.Zero, () => 0);

        Assert.Equal(2, built);
        Assert.Equal(0, code);
    }

    [Fact]
    public void A_standalone_runs_failure_ends_the_process_with_an_error()
    {
        int built = 0;

        int code = ResidentHost.Run(() =>
        {
            built++;
            return Host<Faulting>();
        }, NullLogger.Instance, resident: false, TimeSpan.Zero, () => 0);

        Assert.Equal(1, built);
        Assert.Equal(Program.ExitError, code);
    }

    [Fact]
    public void A_run_that_could_not_be_torn_down_ends_the_process_for_a_new_one()
    {
        int code = ResidentHost.Run(Host<Stopping>, NullLogger.Instance, resident: true, TimeSpan.Zero, () => AgentProcess.RestartExitCode);

        Assert.Equal(AgentProcess.RestartExitCode, code);
    }

    private static IHost Host<TService>() where TService : class, IHostedService
    {
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Services.AddHostedService<TService>();
        return builder.Build();
    }

    // A link that breaks: on .NET 10 the host stops and Run returns as after a stop.
    private sealed class Faulting : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(10, stoppingToken);
            throw new InvalidOperationException("the link broke");
        }
    }

    // A person's Ctrl+C.
    private sealed class Stopping(IHostApplicationLifetime lifetime) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            lifetime.StopApplication();
            return Task.CompletedTask;
        }
    }
}
