using Contracts.Bots;

using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ControlCenter.Bots;

/// <summary>
/// One bot's log as its console shows it: the manager's backlog joined to the fleet hub's stream,
/// with no line lost or repeated where they meet. Lines streamed while a backlog is read are held
/// until it is in. A line has no number, so one already shown is known by its time and text
/// (record equality) and dropped; a backlog read again after a reconnect fills in what was missed,
/// in time order. Lines come from SignalR's threads and the page's.
/// </summary>
public sealed class BotLogBuffer(int capacity = BotLogBuffer.DefaultCapacity)
{
    /// <summary>Twice what the manager keeps (500), so a backlog read again always overlaps what is shown.</summary>
    public const int DefaultCapacity = 1000;

    private readonly Lock sync = new();
    private readonly HashSet<BotLogLine> shown = [];
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

    /// <summary>Adds the backlog, then what streamed while it was read, each line once, in time order.</summary>
    public void Join(IEnumerable<BotLogLine> backlog)
    {
        lock (sync)
        {
            List<BotLogLine> added = [.. backlog.Concat(held ?? []).Where(shown.Add)];
            held = null;
            if (added.Count == 0)
            {
                return;
            }

            // Stable: lines logged in the same instant keep the order they came in.
            lines = [.. lines.Concat(added).OrderBy(line => line.At)];
            Trim();
        }
    }

    /// <summary>Lines from the stream: held while a backlog is read, else added; each line once.</summary>
    public void Add(IEnumerable<BotLogLine> streamed)
    {
        lock (sync)
        {
            if (held is not null)
            {
                held.AddRange(streamed);
                return;
            }

            lines.AddRange(streamed.Where(shown.Add));
            Trim();
        }
    }

    // Under the lock: the oldest lines go once there are more than the capacity.
    private void Trim()
    {
        int excess = lines.Count - capacity;
        if (excess <= 0)
        {
            return;
        }

        foreach (BotLogLine line in lines.Take(excess))
        {
            shown.Remove(line);
        }

        lines.RemoveRange(0, excess);
    }
}
