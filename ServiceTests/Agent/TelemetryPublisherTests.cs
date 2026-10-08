using BotAgent.Runtime;

using Contracts.Bots;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Agent;

/// <summary>
/// The agent's telemetry never stops the agent (known issue 67): a client probe that threw faulted
/// the publisher, and on .NET 10 a faulted background service stops the host and the run under way.
/// </summary>
public sealed class TelemetryPublisherTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("telemetry-").FullName;

    [Fact]
    public async Task A_probe_that_throws_stops_neither_the_telemetry_nor_the_run()
    {
        ThrowingProbe probe = new();
        FakeRuntimeFactory factory = new();
        await using AgentUnderTest agent = await AgentUnderTest.StartAsync(folder, null, factory, probe: probe);

        Assert.True(agent.Host.Start(BotAssignment.ForForever("crossroads_trip.json", 5)).Accepted);
        await Eventually.True(() => probe.Calls >= 1);
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);

        Assert.False(agent.Telemetry.ExecuteTask!.IsCompleted, "the telemetry ended");
        Assert.NotNull(agent.Host.CurrentRun);
        Assert.Null(agent.Telemetry.Take().Game);
    }

    [Fact]
    public async Task A_clock_set_back_does_not_hold_the_probe()
    {
        SteppedClock clock = new();
        CountingProbe probe = new();
        await using AgentUnderTest agent = await AgentUnderTest.StartAsync(folder, null, new FakeRuntimeFactory(), probe: probe, clock: clock);
        await Eventually.True(() => probe.Calls >= 1);

        // Six seconds pass on the monotonic clock while the wall clock is set back an hour.
        clock.Advance(TimeSpan.FromSeconds(6), wallClock: TimeSpan.FromHours(-1));
        agent.Telemetry.Take();

        Assert.Equal(2, probe.Calls);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private sealed class ThrowingProbe : IGameProbe
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public AgentGame? Probe()
        {
            Interlocked.Increment(ref calls);
            throw new FileNotFoundException("Could not find file 'Wow.exe'", "Wow.exe");
        }
    }

    private sealed class CountingProbe : IGameProbe
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public AgentGame? Probe()
        {
            Interlocked.Increment(ref calls);
            return null;
        }
    }

    // A wall clock and a monotonic one that the test moves apart.
    private sealed class SteppedClock : TimeProvider
    {
        private long ticks;
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return Interlocked.Read(ref ticks);
        }

        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }

        public void Advance(TimeSpan monotonic, TimeSpan wallClock)
        {
            Interlocked.Add(ref ticks, monotonic.Ticks);
            now += wallClock;
        }
    }
}
