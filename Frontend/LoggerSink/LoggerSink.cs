using Serilog.Core;
using Serilog.Events;

namespace Frontend;

public sealed class LoggerSink : ILogEventSink
{
    public event Action? OnLogChanged;

    public const int SIZE = 256;
    private const int MOD = SIZE - 1;

    private int callCount;
    private readonly LogEvent[] log = new LogEvent[SIZE];

    public void Emit(LogEvent logEvent)
    {
        log[callCount++ & MOD] = logEvent;
        OnLogChanged?.Invoke();
    }

    /// <summary>The last <see cref="SIZE"/> events at most, newest first.</summary>
    public IEnumerable<LogEvent> NewestFirst()
    {
        int written = callCount;
        // Negative once the count wraps around int, when the buffer is long full.
        int kept = (uint)written < SIZE ? written : SIZE;
        for (int i = 1; i <= kept; i++)
        {
            yield return log[(written - i) & MOD];
        }
    }
}
