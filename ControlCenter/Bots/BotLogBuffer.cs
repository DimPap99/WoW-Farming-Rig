using Contracts.Bots;

using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ControlCenter.Bots;

/// <summary>
/// One bot's log as its console shows it: the manager's backlog joined to the fleet hub's stream,
/// with no line lost or repeated where they meet. Lines streamed while a backlog is read are held
/// until it is in. A line is known by its process and number (<see cref="BotLogLine.Follows"/>): one
/// already shown, or sent again, is dropped, and a backlog read again after a reconnect fills in what
/// was missed, in order. Lines come from SignalR's threads and the page's.
/// </summary>
public sealed class BotLogBuffer(int capacity = BotLogBuffer.DefaultCapacity)
{
    /// <summary>The lines a console keeps: twice what the manager keeps.</summary>
    public const int DefaultCapacity = 1000;

    private readonly Lock sync = new();
    private List<BotLogLine> lines = [];
    private List<BotLogLine>? held;

    /// <summary>The lines, oldest first.</summary>
    public BotLogLine[] Lines
    {
        get
        {
            lock (sync)
            {
                return [.. lines];
            }
        }
    }

    /// <summary>A backlog is being read: streamed lines wait for it.</summary>
    public void BeginBacklog()
    {
        lock (sync)
        {
            held ??= [];
        }
    }

    /// <summary>Adds the backlog, then what streamed while it was read, each line once, in the order they were logged.</summary>
    public void Join(IEnumerable<BotLogLine> backlog)
    {
        lock (sync)
        {
            HashSet<(long, long)> shown = [.. lines.Select(Key)];
            List<BotLogLine> added = [.. backlog.Concat(held ?? []).Where(line => shown.Add(Key(line)))];
            held = null;
            if (added.Count == 0)
            {
                return;
            }

            lines = [.. lines.Concat(added).OrderBy(line => line.Epoch).ThenBy(line => line.Number)];
            Trim();
        }
    }

    /// <summary>Lines from the stream: held while a backlog is read, else added after the last; one that does not follow it is dropped.</summary>
    public void Add(IEnumerable<BotLogLine> streamed)
    {
        lock (sync)
        {
            if (held is not null)
            {
                held.AddRange(streamed);
                return;
            }

            foreach (BotLogLine line in streamed)
            {
                if (lines.Count == 0 || line.Follows(lines[^1]))
                {
                    lines.Add(line);
                }
            }

            Trim();
        }
    }

    private static (long, long) Key(BotLogLine line)
    {
        return (line.Epoch, line.Number);
    }

    // Under the lock: the oldest lines go once there are more than the capacity.
    private void Trim()
    {
        int excess = lines.Count - capacity;
        if (excess > 0)
        {
            lines.RemoveRange(0, excess);
        }
    }
}
