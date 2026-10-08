using Microsoft.AspNetCore.Builder;

using Serilog;
using Serilog.Templates;
using Serilog.Templates.Themes;

namespace ServiceHosting;

/// <summary>Serilog for a service: the console and, unless switched off, a daily file; levels from the <c>Serilog</c> section.</summary>
public static class ServiceLogging
{
    /// <summary>Configuration key for the rolling log file; an empty value turns the file off.</summary>
    public const string FileKey = "LogFile";

    /// <summary>Where the log goes when <see cref="FileKey"/> is not configured at all.</summary>
    public const string DefaultFile = "out.log";

    /// <summary>
    /// The bot hosts' line shape (SharedLib's <c>LogOutputTemplates.Default</c>), the source's last segment
    /// computed here instead of by an enricher. The Pathing Service used SharedLib's without the enricher,
    /// and its lines named no source.
    /// </summary>
    public const string Template = "[{@t:HH:mm:ss:fff} {@l:u1}] {#if IsDefined(SourceContext)}[{Substring(SourceContext, LastIndexOf(SourceContext, '.') + 1),-17}] {#end}{@m}\n{@x}";

    public static WebApplicationBuilder AddServiceLogging(this WebApplicationBuilder builder)
    {
        string? file = builder.Configuration[FileKey] ?? DefaultFile;

        builder.Services.AddSerilog(logger =>
        {
            logger.ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .WriteTo.Console(new ExpressionTemplate(Template, theme: TemplateTheme.Literate));

            if (!string.IsNullOrEmpty(file))
            {
                logger.WriteTo.File(new ExpressionTemplate(Template), file, rollingInterval: RollingInterval.Day);
            }
        }, preserveStaticLogger: true);

        return builder;
    }
}
