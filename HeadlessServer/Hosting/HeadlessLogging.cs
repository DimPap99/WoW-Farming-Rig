using BotAgent.Telemetry;

using Microsoft.Extensions.Configuration;

using Serilog;
using Serilog.Configuration;
using Serilog.Templates;
using Serilog.Templates.Themes;

using SharedLib.Logging;

namespace HeadlessServer.Hosting;

/// <summary>The bot's log: the file, the debugger and the console as always, and the agent's stream to the Control Center.</summary>
public static class HeadlessLogging
{
    public const string LogFile = "headless_out.log";

    /// <summary>Lines the console's queue holds; beyond them the console drops lines, the file keeps them.</summary>
    public const int ConsoleQueue = 10_000;

    public static Serilog.ILogger Create(IConfiguration configuration, AgentLogSink sink)
    {
        return new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.With<ShortSourceContextEnricher>()
            .WriteTo.File(new ExpressionTemplate(LogOutputTemplates.Default),
                path: LogFile,
                rollingInterval: RollingInterval.Day)
            .WriteTo.Debug(new ExpressionTemplate(LogOutputTemplates.Default))
            .WriteTo.Queued(console => console.Console(new ExpressionTemplate(LogOutputTemplates.Default, theme: TemplateTheme.Literate)))
            .WriteTo.Sink(sink)
            .CreateLogger();
    }

    /// <summary>
    /// <paramref name="sink"/> behind a queue of its own: a write that blocks there never holds the
    /// thread that logged. A click into the Windows 10 console starts a selection that blocks every
    /// write to it, which held the bot with a key down and a stop in its log call (known issue 64).
    /// </summary>
    public static LoggerConfiguration Queued(this LoggerSinkConfiguration writeTo, Action<LoggerSinkConfiguration> sink)
    {
        return writeTo.Async(sink, bufferSize: ConsoleQueue, blockWhenFull: false);
    }
}
