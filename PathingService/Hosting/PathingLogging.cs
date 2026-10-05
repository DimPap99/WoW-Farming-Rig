using Microsoft.AspNetCore.Builder;

using Serilog;
using Serilog.Templates;
using Serilog.Templates.Themes;

using SharedLib.Logging;

namespace PathingService.Hosting;

public static class PathingLogging
{
    /// <summary>Configuration key for the rolling log file; an empty value turns the file off.</summary>
    public const string FileKey = "LogFile";

    /// <summary>Where the log goes when <see cref="FileKey"/> is not configured at all.</summary>
    public const string DefaultFile = "out.log";

    /// <summary>
    /// Serilog to the console and, unless switched off, a daily file. Levels come from the
    /// <c>Serilog</c> section, as in the other hosts.
    /// </summary>
    public static WebApplicationBuilder AddPathingLogging(this WebApplicationBuilder builder)
    {
        string? file = builder.Configuration[FileKey] ?? DefaultFile;

        builder.Services.AddSerilog(logger =>
        {
            logger.ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .WriteTo.Console(new ExpressionTemplate(LogOutputTemplates.Default, theme: TemplateTheme.Literate));

            if (!string.IsNullOrEmpty(file))
            {
                logger.WriteTo.File(new ExpressionTemplate(LogOutputTemplates.Default), file,
                    rollingInterval: RollingInterval.Day);
            }
        }, preserveStaticLogger: true);

        return builder;
    }
}
