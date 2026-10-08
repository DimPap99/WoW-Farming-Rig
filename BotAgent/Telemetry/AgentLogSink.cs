using Contracts.Bots;

using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace BotAgent.Telemetry;

/// <summary>
/// The agent's log as lines for the Control Center's console: a Serilog sink beside the file and the
/// console, keeping the last <see cref="Kept"/> lines at <see cref="Minimum"/> or above, numbered, so
/// the publisher sends each once and catches up after the manager was away.
/// </summary>
public sealed class AgentLogSink(LogEventLevel minimum = LogEventLevel.Information) : ILogEventSink
{
    public const int Kept = 2000;

    private const string SourceContext = "SourceContext";

    private readonly Lock sync = new();
    private readonly Queue<(long Number, BotLogLine Line)> lines = new();
    private long next = 1;
    // What reached the manager. The sink outlives the hosts a process builds, and so does this: a host
    // built again resent up to Kept lines when its publisher counted from 0 (known issue 76).
    private long sent;

    public LogEventLevel Minimum => minimum;

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < minimum)
        {
            return;
        }

        string message = Render(logEvent);
        if (logEvent.Exception is { } exception)
        {
            message = $"{message} ({exception.GetType().Name}: {exception.Message})";
        }

        BotLogLine line = new(logEvent.Timestamp, logEvent.Level.ToString(), Source(logEvent), message);
        lock (sync)
        {
            lines.Enqueue((next++, line));
            while (lines.Count > Kept)
            {
                lines.Dequeue();
            }
        }
    }

    /// <summary>Up to <paramref name="max"/> lines not sent yet, oldest first, and the number of the last; see <see cref="MarkSent"/>.</summary>
    public BotLogLine[] ReadUnsent(int max, out long last)
    {
        return ReadAfter(Interlocked.Read(ref sent), max, out last);
    }

    /// <summary>The lines up to <paramref name="last"/> reached the manager.</summary>
    public void MarkSent(long last)
    {
        long seen;
        do
        {
            seen = Interlocked.Read(ref sent);
        }
        while (last > seen && Interlocked.CompareExchange(ref sent, last, seen) != seen);
    }

    /// <summary>Up to <paramref name="max"/> lines after line <paramref name="after"/>, oldest first, and the number of the last.</summary>
    public BotLogLine[] ReadAfter(long after, int max, out long last)
    {
        List<BotLogLine> read = [];
        last = after;
        lock (sync)
        {
            foreach ((long number, BotLogLine line) in lines)
            {
                if (number <= after)
                {
                    continue;
                }

                read.Add(line);
                last = number;
                if (read.Count == max)
                {
                    break;
                }
            }
        }

        return [.. read];
    }

    // The message as the file log shows it: text values without the quotes Serilog's own rendering adds.
    private static string Render(LogEvent logEvent)
    {
        StringWriter writer = new(CultureInfo.InvariantCulture);
        foreach (MessageTemplateToken token in logEvent.MessageTemplate.Tokens)
        {
            if (token is PropertyToken property && logEvent.Properties.TryGetValue(property.PropertyName, out LogEventPropertyValue? value) &&
                value is ScalarValue { Value: string text })
            {
                writer.Write(text);
            }
            else
            {
                token.Render(logEvent.Properties, writer, CultureInfo.InvariantCulture);
            }
        }

        return writer.ToString();
    }

    // The class that logged: the source context's last segment.
    private static string Source(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue(SourceContext, out LogEventPropertyValue? value) &&
            value is ScalarValue { Value: string context } && context.Length > 0)
        {
            int dot = context.LastIndexOf('.');
            return dot >= 0 ? context[(dot + 1)..] : context;
        }

        return string.Empty;
    }
}
