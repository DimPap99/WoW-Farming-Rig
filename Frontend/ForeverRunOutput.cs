using Core.Forever;

namespace Frontend;

/// <summary>One line of a run's output, numbered in the order it came.</summary>
public readonly record struct ForeverRunLine(long Number, string Text);

/// <summary>
/// What a run printed, as the runs page shows it: the latest lines and frame
/// snapshots, numbered in the order they came, and what the
/// <see cref="ForeverRunProtocol"/> lines among them say. Not thread-safe;
/// <see cref="ForeverRunHost"/> calls it under its lock.
/// </summary>
public sealed class ForeverRunOutput
{
    /// <summary>Output lines kept for the page, frame snapshots apart.</summary>
    public const int KeptLines = 400;
    /// <summary>Frame snapshots kept: one comes every two seconds, and an old one says little.</summary>
    public const int KeptSnapshots = 30;

    private readonly Queue<ForeverRunLine> lines = new();
    private readonly Queue<ForeverRunLine> snapshots = new();
    private long lineNumber;

    /// <summary>The latest phase line: the activity, its step and the route's progress.</summary>
    public string Phase { get; private set; } = string.Empty;
    /// <summary>The run's closing line: kills, loot and its status.</summary>
    public string Summary { get; private set; } = string.Empty;
    /// <summary>The runner asked for a click into the game and has not started a phase since.</summary>
    public bool ClickPrompted { get; private set; }

    /// <summary>Forgets the last run's output, for the next.</summary>
    public void Clear()
    {
        lines.Clear();
        snapshots.Clear();
        Phase = Summary = string.Empty;
        ClickPrompted = false;
    }

    public void Add(string line)
    {
        bool snapshot = line.StartsWith(ForeverRunProtocol.SnapshotPrefix, StringComparison.Ordinal);
        Queue<ForeverRunLine> kept = snapshot ? snapshots : lines;
        kept.Enqueue(new(++lineNumber, line));
        while (kept.Count > (snapshot ? KeptSnapshots : KeptLines))
            kept.Dequeue();

        if (line.StartsWith(ForeverRunProtocol.PhasePrefix, StringComparison.Ordinal))
        {
            Phase = line[ForeverRunProtocol.PhasePrefix.Length..].Trim();
            ClickPrompted = false;
        }
        else if (line.StartsWith(ForeverRunProtocol.SummaryPrefix, StringComparison.Ordinal))
        {
            Summary = line;
        }
        else if (line.StartsWith(ForeverRunProtocol.ClickPrompt, StringComparison.Ordinal))
        {
            ClickPrompted = true;
        }
    }

    /// <summary>The kept lines in order, with the frame snapshots when <paramref name="withSnapshots"/>.</summary>
    public IReadOnlyList<ForeverRunLine> Lines(bool withSnapshots)
    {
        return withSnapshots ? [.. lines.Concat(snapshots).OrderBy(line => line.Number)] : [.. lines];
    }
}
