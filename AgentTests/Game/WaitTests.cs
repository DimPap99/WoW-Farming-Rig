using Core;

using System;
using System.Diagnostics;
using System.Threading;

using Xunit;

namespace AgentTests.Game;

/// <summary>
/// The wait <c>BotController</c>'s constructor makes for the addon to read the player's class (known
/// issue 23): it took no token, and a stop, which stops the thread that brings frames, left it timing
/// out for good, so a run stopped then could only be abandoned.
/// </summary>
public sealed class WaitTests
{
    [Fact]
    public void A_cancelled_token_ends_the_wait_at_once()
    {
        using ManualResetEventSlim frames = new(false);
        using CancellationTokenSource cts = new();
        Wait wait = new(frames, cts);
        cts.Cancel();
        Stopwatch watch = Stopwatch.StartNew();

        Assert.Throws<OperationCanceledException>(() => wait.Update(5000, cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"waited {watch.Elapsed}");
    }

    [Fact]
    public void A_frame_ends_the_wait_and_is_taken()
    {
        using ManualResetEventSlim frames = new(true);
        using CancellationTokenSource cts = new();
        Wait wait = new(frames, cts);

        Assert.True(wait.Update(5000, cts.Token));
        Assert.False(frames.IsSet);
    }
}
