using Core;
using Core.Forever;

using SharedLib.Forever;

using System.Diagnostics;

using Xunit;

namespace AgentTests.Forever;

/// <summary>
/// Whether a task's <see cref="ForeverUntil"/> holds, now that the condition is data in SharedLib and
/// the check an extension in Core: the same cases as the Forever reader's logic checks
/// (LogicTest.Roam.cs).
/// </summary>
public sealed class ForeverUntilChecksTests
{
    [Fact]
    public void Minutes_count_from_when_the_task_began()
    {
        ForeverTaskProgress started = new();
        bool beforeStart = ForeverUntil.AfterMinutes(0).IsMet(started, default);
        started.Start(Stopwatch.GetTimestamp() - (long)(61 * Stopwatch.Frequency));

        Assert.False(beforeStart);
        Assert.True(ForeverUntil.AfterMinutes(1).IsMet(started, default));
        Assert.False(ForeverUntil.AfterMinutes(2).IsMet(started, default));
    }

    [Fact]
    public void A_level_holds_once_the_player_has_it()
    {
        VisualBarSample level7 = new VisualBarSample(1, 1, 1, 0.5, 0, 0, 320) with { PlayerIdentity = new(3, 8, 7) };

        Assert.True(ForeverUntil.AtLevel(7).IsMet(new(), level7));
        Assert.False(ForeverUntil.AtLevel(8).IsMet(new(), level7));
    }

    [Fact]
    public void Kills_hold_at_the_count_and_never_without_a_condition()
    {
        ForeverTaskProgress progress = new() { Kills = 3 };

        Assert.True(ForeverUntil.AfterKills(3).IsMet(progress, default));
        Assert.False(ForeverUntil.AfterKills(4).IsMet(progress, default));
        Assert.False(ForeverUntil.Never.IsMet(progress, default));
    }
}
