using ControlCenter.Bots;

using Contracts.Bots;

using System;
using System.Linq;

using Xunit;

namespace ServiceTests.Fleet;

/// <summary>A console's log where the manager's backlog meets the fleet hub's stream: no line lost, none repeated.</summary>
public sealed class BotLogBufferTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 20, 0, 0, TimeSpan.Zero);

    private static readonly long Epoch = Start.ToUnixTimeMilliseconds();

    [Fact]
    public void Lines_streamed_while_the_backlog_is_read_follow_it_each_once()
    {
        BotLogBuffer buffer = new();

        buffer.BeginBacklog();
        buffer.Add([Line(3), Line(4)]);
        buffer.Join([Line(1), Line(2), Line(3)]);

        Assert.Equal([1, 2, 3, 4], Numbers(buffer));
    }

    [Fact]
    public void A_streamed_batch_the_backlog_already_holds_is_dropped()
    {
        BotLogBuffer buffer = new();
        buffer.BeginBacklog();
        buffer.Join([Line(1), Line(2)]);

        // The manager kept the lines before it sent them on: a read can see them, then the stream.
        buffer.Add([Line(2), Line(3)]);
        buffer.Add([Line(3)]);

        Assert.Equal([1, 2, 3], Numbers(buffer));
    }

    [Fact]
    public void A_backlog_read_again_after_a_reconnect_fills_in_what_was_missed_in_time_order()
    {
        BotLogBuffer buffer = new();
        buffer.BeginBacklog();
        buffer.Join([Line(1), Line(2)]);
        // Streamed after the connection came back, before the backlog was asked for again.
        buffer.Add([Line(6)]);

        buffer.BeginBacklog();
        buffer.Add([Line(7)]);
        buffer.Join([Line(2), Line(3), Line(4), Line(5), Line(6)]);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7], Numbers(buffer));
    }

    // The review's V45: lines an agent sent again, up to 2000 old, were shown after the newest until
    // the next backlog read.
    [Fact]
    public void Lines_sent_again_are_dropped_and_never_shown_out_of_order()
    {
        BotLogBuffer buffer = new(capacity: 4);
        buffer.BeginBacklog();
        buffer.Join([.. Enumerable.Range(1, 6).Select(Line)]);

        buffer.Add([Line(1), Line(2), Line(7)]);

        Assert.Equal([4, 5, 6, 7], Numbers(buffer));
    }

    [Fact]
    public void A_new_agent_process_numbers_its_lines_from_one_after_the_old_ones()
    {
        BotLogBuffer buffer = new();
        buffer.BeginBacklog();
        buffer.Join([Line(1), Line(2)]);

        BotLogLine restarted = new(Epoch + 60_000, 1, Start.AddMinutes(1), "Information", "Program", "line 100");
        buffer.Add([restarted]);
        buffer.BeginBacklog();
        buffer.Join([Line(2), restarted]);

        Assert.Equal([1, 2, 100], Numbers(buffer));
    }

    [Fact]
    public void The_oldest_lines_go_beyond_the_capacity_and_may_not_come_back_twice()
    {
        BotLogBuffer buffer = new(capacity: 3);
        buffer.BeginBacklog();

        buffer.Join([.. Enumerable.Range(1, 5).Select(Line)]);
        buffer.Add([Line(5), Line(6)]);

        Assert.Equal([4, 5, 6], Numbers(buffer));
    }

    // Line n of one agent process: logged n seconds after the start, its text naming it.
    private static BotLogLine Line(int n)
    {
        return new BotLogLine(Epoch, n, Start.AddSeconds(n), "Information", "ForeverRun", $"line {n}");
    }

    private static int[] Numbers(BotLogBuffer buffer)
    {
        return [.. buffer.Lines.Select(line => int.Parse(line.Message["line ".Length..], System.Globalization.CultureInfo.InvariantCulture))];
    }
}
