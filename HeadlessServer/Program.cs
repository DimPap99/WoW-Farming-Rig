using BotAgent.Telemetry;

using CommandLine;

using HeadlessServer.Hosting;
using HeadlessServer.Runtimes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog;
using Serilog.Extensions.Logging;

namespace HeadlessServer;

/// <summary>
/// The bot without a UI, and this PC's resident bot agent. A class profile on the command line runs
/// at once, as it always has; <c>--agent</c> keeps the process for the Bot Manager's assignments.
/// It runs on the Generic Host, so Ctrl+C or a logoff stops it cleanly: a run releases input first.
/// </summary>
public static class Program
{
    public const string SettingsFile = "headless_appsettings";
    public const string EnvironmentVariable = "ASPNETCORE_ENVIRONMENT";

    public const int ExitOk = 0;
    public const int ExitError = 1;

    public static int Main(string[] args)
    {
        string? environment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile($"{SettingsFile}.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"{SettingsFile}.{environment}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .AddCommandLine(HeadlessArguments.ForConfiguration(args))
            .Build();

        AgentLogSink sink = new();
        Log.Logger = HeadlessLogging.Create(configuration, sink);
        int exitCode;
        try
        {
            exitCode = Run(args, environment, configuration, sink);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "HeadlessServer stopped on an error");
            exitCode = ExitError;
        }
        finally
        {
            Log.CloseAndFlush();
        }

        // Also ends whatever thread a run that could not be torn down left behind.
        Environment.Exit(exitCode);
        return exitCode;
    }

    private static int Run(string[] args, string? environment, IConfiguration configuration, AgentLogSink sink)
    {
        using SerilogLoggerFactory loggerFactory = new(Log.Logger, dispose: false);
        Microsoft.Extensions.Logging.ILogger log = loggerFactory.CreateLogger(nameof(Program));
        if (log.IsEnabled(LogLevel.Information))
        {
            log.LogInformation("Hosting environment: {Environment}", environment ?? Environments.Production);
            log.LogInformation("{Language} {Timestamp}", Thread.CurrentThread.CurrentCulture.TwoLetterISOLanguageName, DateTimeOffset.Now);
        }

        if (!TryParse(args, log, out RunOptions? options, out int parseExit))
        {
            KeepWindowOpen();
            return parseExit;
        }

        if (options.LoadOnly)
        {
            return ClassicRuntime.LoadOnly(options.ClassConfig!, options, configuration, loggerFactory) ? ExitOk : ExitError;
        }

        using IHost host = HeadlessHost.Build(configuration, environment, options, sink);
        host.Run();
        return Environment.ExitCode;
    }

    // --help and --version come back as NotParsed too, but they are a satisfied request.
    private static bool TryParse(string[] args, Microsoft.Extensions.Logging.ILogger log, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RunOptions? options,
        out int exitCode)
    {
        bool helpOrVersion = false;
        ParserResult<RunOptions> parsed = Parser.Default.ParseArguments<RunOptions>(HeadlessArguments.ForParser(args))
            .WithNotParsed(errors =>
            {
                foreach (Error error in errors)
                {
                    if (error is HelpRequestedError or HelpVerbRequestedError or VersionRequestedError)
                    {
                        helpOrVersion = true;
                        continue;
                    }

                    log.LogError("{Error}", error);
                }
            });

        options = parsed.Tag == ParserResultType.Parsed ? parsed.Value : null;
        if (options is null)
        {
            exitCode = helpOrVersion ? ExitOk : ExitError;
            return false;
        }

        if (options.Problem is string problem)
        {
            log.LogError("{Problem}", problem);
            options = null;
            exitCode = ExitError;
            return false;
        }

        exitCode = ExitOk;
        return true;
    }

    // Keeps the window open when launched by double-click. ReadKey throws when stdin is
    // redirected or no console is attached; run.bat ends with pause anyway.
    private static void KeepWindowOpen()
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        try
        {
            Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
            // No console to read from.
        }
    }
}
