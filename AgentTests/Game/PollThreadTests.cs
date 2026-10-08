using Game;

using System;
using System.Threading;

using Xunit;

namespace AgentTests.Game;

/// <summary>
/// <see cref="PollThread"/>, the thread <c>WowProcess</c> polls the client on (known issue 63): it
/// waited on the given token source's handle, and a source disposed without being cancelled (a
/// Classic run whose bot failed to build) threw on that thread and ended the agent's process.
/// </summary>
public sealed class PollThreadTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public void It_outlives_the_token_source_it_was_given_being_disposed()
    {
        CancellationTokenSource given = new();
        int ticks = 0;
        Exception? failure = null;
        using PollThread poll = new(nameof(PollThreadTests), Interval, () => Interlocked.Increment(ref ticks), ex => failure = ex, given.Token);

        given.Dispose();
        int seen = Volatile.Read(ref ticks);

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= seen + 3, Deadline), "it stopped ticking");
        Assert.Null(failure);
        Assert.True(poll.IsAlive);
    }

    [Fact]
    public void A_tick_that_throws_is_reported_and_the_next_one_runs()
    {
        int ticks = 0;
        Exception? failure = null;
        using PollThread poll = new(nameof(PollThreadTests), Interval, () =>
        {
            if (Interlocked.Increment(ref ticks) == 1)
            {
                throw new InvalidOperationException("the client went away");
            }
        }, ex => failure = ex, CancellationToken.None);

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 3, Deadline), "it stopped after the throw");
        Assert.IsType<InvalidOperationException>(failure);
    }

    [Fact]
    public void Dispose_ends_the_thread_before_it_returns()
    {
        PollThread poll = new(nameof(PollThreadTests), TimeSpan.FromMinutes(1), () => { }, _ => { }, CancellationToken.None);

        poll.Dispose();

        Assert.False(poll.IsAlive);
    }

    [Fact]
    public void A_cancelled_token_ends_it()
    {
        using CancellationTokenSource given = new();
        using PollThread poll = new(nameof(PollThreadTests), TimeSpan.FromMinutes(1), () => { }, _ => { }, given.Token);

        given.Cancel();

        Assert.True(SpinWait.SpinUntil(() => !poll.IsAlive, Deadline), "it kept running");
    }
}
