using System.Globalization;
using System.Text.RegularExpressions;

using Core.Forever;

namespace Frontend;

/// <summary>What a line of a run's output is, for the runs page to style it.</summary>
public enum ForeverRunLineKind
{
    /// <summary>The runner's own text: the route, the profile, the preflight.</summary>
    Text,
    /// <summary>A log line, <c>[category] message</c>: the Forever runtime's decisions, or a warning from elsewhere.</summary>
    Log,
    /// <summary>A phase line: the activity, its step and the route's progress.</summary>
    Phase,
    /// <summary>The closing line: kills, loot and the status.</summary>
    Summary,
    /// <summary>A frame snapshot.</summary>
    Snapshot,
    /// <summary>The runner waits for a click into the game.</summary>
    Prompt,
    /// <summary>Something went wrong: an error, a failure, a refusal, an exception and its stack.</summary>
    Problem
}

/// <summary>
/// One line of a run's output, numbered in the order it came. A log line's
/// <see cref="Source"/> is its category's class name, and <see cref="Message"/>
/// is the line without the category.
/// </summary>
public readonly record struct ForeverRunLine(long Number, string Text, ForeverRunLineKind Kind,
    string Source = "", string Message = "");

/// <summary>
/// What a run printed, as the runs page shows it: the latest lines and frame
/// snapshots, numbered in the order they came, and what the
/// <see cref="ForeverRunProtocol"/> lines among them say. Not thread-safe;
/// <see cref="ForeverRunHost"/> calls it under its lock.
/// </summary>
public sealed partial class ForeverRunOutput
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
    /// <summary>The route step the latest phase is on, from 1; 0 before the first.</summary>
    public int CurrentTask { get; private set; }
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
        CurrentTask = 0;
        ClickPrompted = false;
    }

    public void Add(string line)
    {
        ForeverRunLine numbered = Classify(++lineNumber, line);
        bool snapshot = numbered.Kind == ForeverRunLineKind.Snapshot;
        Queue<ForeverRunLine> kept = snapshot ? snapshots : lines;
        kept.Enqueue(numbered);
        while (kept.Count > (snapshot ? KeptSnapshots : KeptLines))
            kept.Dequeue();

        switch (numbered.Kind)
        {
            case ForeverRunLineKind.Phase:
                Phase = line[ForeverRunProtocol.PhasePrefix.Length..].Trim();
                if (TaskRegex().Match(Phase) is { Success: true } task)
                    CurrentTask = int.Parse(task.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
                ClickPrompted = false;
                break;
            case ForeverRunLineKind.Summary:
                Summary = line;
                break;
            case ForeverRunLineKind.Prompt:
                ClickPrompted = true;
                break;
        }
    }

    /// <summary>The kept lines in order, with the frame snapshots when <paramref name="withSnapshots"/>.</summary>
    public IReadOnlyList<ForeverRunLine> Lines(bool withSnapshots)
    {
        return withSnapshots ? [.. lines.Concat(snapshots).OrderBy(line => line.Number)] : [.. lines];
    }

    /// <summary>
    /// The summary's <c>name: value</c> fields, in order, without the
    /// "Autonomous" prefix: kills, deaths observed, corpses looted, status.
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> SummaryFields(string summary)
    {
        List<(string, string)> fields = [];
        string body = summary.StartsWith(SummaryNamePrefix, StringComparison.Ordinal) ? summary[SummaryNamePrefix.Length..] : summary;
        foreach (string part in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':');
            if (colon > 0)
                fields.Add((part[..colon].Trim(), part[(colon + 1)..].Trim()));
        }
        return fields;
    }

    // "Autonomous kills: 3; ..." names its first field after this.
    private const string SummaryNamePrefix = "Autonomous ";

    private static ForeverRunLine Classify(long number, string line)
    {
        if (line.StartsWith(ForeverRunProtocol.SnapshotPrefix, StringComparison.Ordinal))
            return new(number, line, ForeverRunLineKind.Snapshot, Message: line);
        if (line.StartsWith(ForeverRunProtocol.PhasePrefix, StringComparison.Ordinal))
            return new(number, line, ForeverRunLineKind.Phase, Message: line);
        if (line.StartsWith(ForeverRunProtocol.SummaryPrefix, StringComparison.Ordinal))
            return new(number, line, ForeverRunLineKind.Summary, Message: line);
        if (line.StartsWith(ForeverRunProtocol.ClickPrompt, StringComparison.Ordinal))
            return new(number, line, ForeverRunLineKind.Prompt, Message: line);

        // The runner's logger writes "[category] message exception".
        string source = string.Empty;
        string message = line;
        int close = line.IndexOf("] ", StringComparison.Ordinal);
        if (line.StartsWith('[') && close > 1)
        {
            string category = line[1..close];
            source = category[(category.LastIndexOf('.') + 1)..];
            message = line[(close + 2)..];
        }

        ForeverRunLineKind kind = ProblemRegex().IsMatch(line) ? ForeverRunLineKind.Problem
            : source.Length > 0 ? ForeverRunLineKind.Log
            : ForeverRunLineKind.Text;
        return new(number, line, kind, source, message);
    }

    [GeneratedRegex(@"\btask (\d+) of \d+")]
    private static partial Regex TaskRegex();

    // An exception's text and its stack frames, and the words of a failure.
    [GeneratedRegex(@"exception|error|fail|refus|^\s+at |^\s*---", RegexOptions.IgnoreCase)]
    private static partial Regex ProblemRegex();
}
