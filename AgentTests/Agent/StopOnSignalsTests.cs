using HeadlessServer.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace AgentTests.Agent;

/// <summary>
/// A clean stop whichever way the agent is stopped (known issue 65): closing its window (SIGHUP,
/// CTRL_CLOSE on Windows) and the session ending stopped nothing, and Windows ended the process with
/// a key held. The signal is held until the host has stopped, since Windows ends the process once it
/// returns.
/// </summary>
public sealed class StopOnSignalsTests
{
    private const int SIGHUP = 1;

    [Fact]
    public async Task A_signal_stops_the_host_and_returns_once_it_stopped()
    {
        using IHost host = Host<SlowStop>(TimeSpan.FromMilliseconds(300));
        await host.StartAsync(TestContext.Current.CancellationToken);
        Task running = host.WaitForShutdownAsync(TestContext.Current.CancellationToken);
        StopOnSignals signals = Signals(host);

        bool stopped = await Task.Run(() => signals.StopAndWait("a test", TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);

        Assert.True(stopped);
        Assert.True(host.Services.GetRequiredService<SlowStop>().Ended);
        await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_signal_is_held_no_longer_than_its_bound()
    {
        using IHost host = Host<SlowStop>(TimeSpan.FromSeconds(3));
        await host.StartAsync(TestContext.Current.CancellationToken);
        Task running = host.WaitForShutdownAsync(TestContext.Current.CancellationToken);
        Stopwatch watch = Stopwatch.StartNew();

        bool stopped = await Task.Run(() => Signals(host).StopAndWait("a test", TimeSpan.FromMilliseconds(200)),
            TestContext.Current.CancellationToken);

        Assert.False(stopped);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"held {watch.Elapsed}");
        await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>The real agent, idle, closing its terminal: it ends cleanly with exit code 0. It was ended by the signal (129).</summary>
    [Fact]
    public async Task The_agent_stops_cleanly_when_its_terminal_closes()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("SIGHUP is a Linux and macOS signal; on Windows it is the window closing, which a test cannot send");
        }

        string folder = Directory.CreateTempSubdirectory("stop-on-signals-").FullName;
        try
        {
            StringBuilder output = new();
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using Process agent = new()
            {
                StartInfo = new ProcessStartInfo("dotnet", [typeof(HeadlessServer.Program).Assembly.Location, "--agent"])
                {
                    WorkingDirectory = folder,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            agent.OutputDataReceived += (_, line) =>
            {
                lock (output)
                {
                    output.AppendLine(line.Data);
                }

                if (line.Data?.Contains("Application started", StringComparison.Ordinal) == true)
                {
                    started.TrySetResult();
                }
            };
            agent.Start();
            agent.BeginOutputReadLine();
            agent.BeginErrorReadLine();

            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(0, kill(agent.Id, SIGHUP));
            bool exited = agent.WaitForExit(TimeSpan.FromSeconds(30));
            if (!exited)
            {
                agent.Kill();
            }

            string seen;
            lock (output)
            {
                seen = output.ToString();
            }

            Assert.True(exited, "the agent did not end");
            Assert.Equal(0, agent.ExitCode);
            Assert.Contains("Stopping: the window closed", File.ReadAllText(Path.Combine(folder, LogFileOfToday(folder))));
            Assert.Contains("Application is shutting down", seen);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    // The daily file Serilog rolled to: headless_out<yyyyMMdd>.log.
    private static string LogFileOfToday(string folder)
    {
        return Path.GetFileName(Directory.GetFiles(folder, "headless_out*.log")[0]);
    }

    private static StopOnSignals Signals(IHost host)
    {
        foreach (IHostedService service in host.Services.GetServices<IHostedService>())
        {
            if (service is StopOnSignals signals)
            {
                return signals;
            }
        }

        throw new InvalidOperationException("no StopOnSignals in the host");
    }

    private static IHost Host<T>(TimeSpan stopTakes) where T : class, IHostedService
    {
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        builder.Services.AddSingleton(new SlowStop(stopTakes));
        builder.Services.AddHostedService<StopOnSignals>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SlowStop>());
        return builder.Build();
    }

    // A service whose stop takes a while, as a run's release and last report do.
    private sealed class SlowStop(TimeSpan takes) : IHostedService
    {
        public bool Ended { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(takes, CancellationToken.None);
            Ended = true;
        }
    }
}
