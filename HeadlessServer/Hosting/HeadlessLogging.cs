using BotAgent.Telemetry;

using Microsoft.Extensions.Configuration;

using Serilog;
using Serilog.Templates;
using Serilog.Templates.Themes;

using SharedLib.Logging;

namespace HeadlessServer.Hosting;

/// <summary>The bot's log: the file, the debugger and the console as always, and the agent's stream to the Control Center.</summary>
public static class HeadlessLogging
{
    public const string LogFile = "headless_out.log";

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
            .WriteTo.Console(new ExpressionTemplate(LogOutputTemplates.Default, theme: TemplateTheme.Literate))
            .WriteTo.Sink(sink)
            .CreateLogger();
    }
}
