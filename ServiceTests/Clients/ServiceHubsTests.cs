using Contracts.Bots;

using Microsoft.AspNetCore.SignalR.Client;

using ServiceClients;

using ServiceTests.Agent;
using ServiceTests.Api;
using ServiceTests.Bots;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Clients;

/// <summary>
/// The one loop every hub client stays connected with (<see cref="ServiceHubs"/>): the agent's link and the
/// Control Center's two feeds each had a copy, and only the link's varied its waits.
/// </summary>
public sealed class ServiceHubsTests
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(9, 5)]
    public void The_wait_starts_over_after_a_session_and_grows_with_each_failure_in_a_row(int failures, double seconds)
    {
        TimeSpan[] waits = [.. Enumerable.Range(0, 200).Select(_ => ServiceHubs.Wait(Backoff, failures))];

        Assert.All(waits, wait => Assert.InRange(wait.TotalSeconds, seconds * 0.8, seconds * 1.2));
        // Varied, so the clients that lost a service together do not dial it in step.
        Assert.True(waits.Distinct().Count() > 1);
    }

    [Fact]
    public async Task An_outage_is_reported_once_and_the_connection_comes_back_by_itself()
    {
        using TempDataRoot data = new();
        ManagerSlot slot = new();
        int unreachable = 0;
        int sessions = 0;
        TaskCompletionSource up = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using HubConnection hub = ServiceHubs.Build(ManagerSlot.Address, BotManagerApi.FleetHub, null, InMemoryHub.Over(slot.Handler));

        Task loop = ServiceHubs.StayConnectedAsync(hub, [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50)],
            async (_, closed, token) =>
            {
                Interlocked.Increment(ref sessions);
                up.TrySetResult();
                await closed.WaitAsync(token);
                return true;
            },
            _ => Interlocked.Increment(ref unreachable), stop.Token);

        // No manager: attempt after attempt fails, and the outage is reported once.
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref unreachable));

        using (slot.Bring(new BotManagerFactory(data.Root)))
        {
            await up.Task.WaitAsync(Eventually.Wait, TestContext.Current.CancellationToken);
            slot.Clear();
        }

        // The manager gone again: a new outage, reported once more.
        await Eventually.True(() => Volatile.Read(ref unreachable) == 2);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref unreachable));
        Assert.Equal(1, Volatile.Read(ref sessions));

        await stop.CancelAsync();
        await loop.WaitAsync(Eventually.Wait, TestContext.Current.CancellationToken);
    }
}
