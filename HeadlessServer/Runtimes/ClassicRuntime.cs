using BotAgent.Runtime;

using Contracts.Bots;

using Core;

using Game;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SharedLib;

using static System.Diagnostics.Stopwatch;

namespace HeadlessServer.Runtimes;

/// <summary>
/// A Classic run: the bot HeadlessServer always ran, composed for this run as its <c>Program</c>
/// composed it once (the command line's options, the navmesh settings, <c>AddWoWProcess</c>,
/// <c>AddCoreBase</c>, <c>AddCoreNormal</c>), then the same start: the readers' initial state, the
/// class profile (and a path in place of its first), and the bot turned on. Stopping turns the bot
/// off, which releases what it holds, before the composition is disposed.
/// </summary>
public sealed partial class ClassicRuntime : IBotRuntime
{
    private const int CellUpdateTicks = 5 * 2;
    private static readonly TimeSpan ActiveCheck = TimeSpan.FromMilliseconds(250);

    private readonly ClassicAssignment assignment;
    private readonly int processId;
    private readonly RunOptions options;
    private readonly IConfiguration configuration;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private volatile ServiceProvider? provider;
    private volatile Readers? readers;
    private volatile bool started;

    private ClassicRuntime(ClassicAssignment assignment, int processId, RunOptions options, IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        this.assignment = assignment;
        this.processId = processId;
        this.options = options;
        this.configuration = configuration;
        this.loggerFactory = loggerFactory;
        logger = loggerFactory.CreateLogger<ClassicRuntime>();
        Summary = assignment.PathOverride is null ? $"profile {assignment.ClassProfile}"
            : $"profile {assignment.ClassProfile} on {assignment.PathOverride}";
    }

    public BotRuntimeKind Kind => BotRuntimeKind.Classic;

    public string Summary { get; }

    /// <summary>A runtime for the profile, once the addon is configured on this PC and the files are there.</summary>
    public static bool TryCreate(ClassicAssignment assignment, AgentGame game, DataConfig data, RunOptions options,
        IConfiguration configuration, ILoggerFactory loggerFactory, out IBotRuntime runtime, out string refusal)
    {
        runtime = null!;
        if (MissingConfiguration() is string missing)
        {
            refusal = missing;
            return false;
        }

        if (!File.Exists(Path.Join(data.Class, assignment.ClassProfile)))
        {
            refusal = $"the class profile {assignment.ClassProfile} is not in this PC's Json/class";
            return false;
        }

        if (assignment.PathOverride is { } path && !File.Exists(Path.Join(data.Path, path)))
        {
            refusal = $"the path {path} is not in this PC's Json/path";
            return false;
        }

        runtime = new ClassicRuntime(assignment, game.ProcessId, options, configuration, loggerFactory);
        refusal = string.Empty;
        return true;
    }

    /// <summary>
    /// HeadlessServer's <c>--loadonly</c>: composes the bot for the running client and loads
    /// <paramref name="profile"/>, without starting anything. True when it loads.
    /// </summary>
    public static bool LoadOnly(string profile, RunOptions options, IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        ClassicRuntime runtime = new(new ClassicAssignment(profile, null), options.Pid, options, configuration, loggerFactory);
        string refusal = MissingConfiguration() ?? string.Empty;
        using ServiceProvider? composed = refusal.Length == 0 ? runtime.Compose(out refusal) : null;
        if (composed is null)
        {
            LogRefused(runtime.logger, refusal);
            return false;
        }

        return composed.GetRequiredService<IBotController>().LoadClassProfile(profile);
    }

    /// <summary>Why the addon's configuration is not here for a Classic run, or null: both files beside HeadlessServer.</summary>
    public static string? MissingConfiguration()
    {
        return FrameConfig.Exists() && AddonConfig.Exists()
            ? null
            : $"{AddonConfigMeta.DefaultFileName} and {FrameConfigMeta.DefaultFilename} are not beside HeadlessServer: " +
              "configure the addon in BlazorServer, then copy them here (install.bat)";
    }

    public BotRunResult Run(CancellationToken stop)
    {
        try
        {
            ServiceProvider? composed = Compose(out string refusal);
            if (composed is null)
            {
                return new BotRunResult(BotRunOutcome.Refused, refusal);
            }

            provider = composed;
            // Waits until the addon reads the player's class, as HeadlessServer always did. A stop meanwhile
            // cancels the composition's token, which ends that wait (known issue 23); only while it is built:
            // after it, a stop turns the bot off first, and the token is cancelled when it is torn down.
            IBotController controller;
            using (stop.Register(composed.GetRequiredService<CancellationTokenSource>().Cancel))
            {
                controller = composed.GetRequiredService<IBotController>();
            }

            readers = new Readers(controller, composed.GetRequiredService<PlayerReader>(), composed.GetRequiredService<AddonBits>(),
                composed.GetRequiredService<SessionStat>());
            if (stop.IsCancellationRequested)
            {
                return new BotRunResult(BotRunOutcome.Stopped, "stopped before it started");
            }

            InitState(composed);
            if (!controller.LoadClassProfile(assignment.ClassProfile))
            {
                return new BotRunResult(BotRunOutcome.Refused, $"the class profile {assignment.ClassProfile} did not load: see the log");
            }

            if (assignment.PathOverride is { } path)
            {
                controller.LoadPathProfile(new Dictionary<int, string> { [0] = path });
            }

            if (stop.IsCancellationRequested)
            {
                return new BotRunResult(BotRunOutcome.Stopped, "stopped before it started");
            }

            controller.ToggleBotStatus();
            started = true;
            while (!stop.IsCancellationRequested && controller.IsBotActive)
            {
                stop.WaitHandle.WaitOne(ActiveCheck);
            }

            bool stopped = stop.IsCancellationRequested;
            // Off first: the controller releases what it holds; only then is anything disposed.
            if (controller.IsBotActive)
            {
                controller.ToggleBotStatus();
            }

            return stopped
                ? new BotRunResult(BotRunOutcome.Stopped, "stopped")
                : new BotRunResult(BotRunOutcome.Complete, "the bot stopped itself");
        }
        catch (OperationCanceledException) when (!started && stop.IsCancellationRequested)
        {
            return new BotRunResult(BotRunOutcome.Stopped, "stopped before it started");
        }
        catch (Exception ex) when (!started)
        {
            LogRefused(logger, ex.Message);
            return new BotRunResult(BotRunOutcome.Refused, $"the bot did not start: {ex.Message}");
        }
    }

    public void ForceRelease()
    {
        try
        {
            provider?.GetService<WowProcessInput>()?.Reset();
        }
        catch (ObjectDisposedException)
        {
            // Torn down meanwhile: nothing is held.
        }
    }

    public BotTelemetry Read()
    {
        if (readers is not { } r)
        {
            return new BotTelemetry(BotRunState.Starting, Message: "attaching to the client and waiting for the addon");
        }

        PlayerReader player = r.Player;
        BotCharacter character = new(player.Class.ToString(), player.Level.Value, player.Race.ToString());
        BotVitals vitals = new(player.HealthPercent(), player.PTPercentage(), r.Bits.Target() ? player.TargetHealthPercent() : null,
            r.Bits.Combat());
        BotPosition position = new(player.UIMapId.Value, player.MapX, player.MapY);
        BotRunState state = !started ? BotRunState.Starting : r.Controller.IsBotActive ? BotRunState.Running : BotRunState.Stopping;
        return new BotTelemetry(state, character, vitals, position, r.Controller.GoapAgent?.CurrentGoal?.Name, null,
            new BotProgress(r.Session.Kills, r.Session.Deaths, 0));
    }

    public void Dispose()
    {
        readers = null;
        provider?.Dispose();
        provider = null;
    }

    // The bot as HeadlessServer composed it, for the client the probe found; null with why when the
    // client is not one a Classic run can play.
    private ServiceProvider? Compose(out string refusal)
    {
        ILogger log = loggerFactory.CreateLogger("Program");
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddSingleton<ILogger>(loggerFactory.CreateLogger(string.Empty));
        services.AddLogging();
        services.AddSingleton(options);
        services.AddStartupConfigFactories();
        // The client the probe found, not whichever WowProcess would pick first.
        services.AddSingleton<IOptions<StartupConfigPid>>(Options.Create(new StartupConfigPid { Id = processId }));
        services.Configure<NavmeshBakeOptions>(configuration.GetSection(NavmeshBakeOptions.Position));
        services.Configure<NavmeshQueryOptions>(configuration.GetSection(NavmeshQueryOptions.Position));
        services.Configure<SplineFollowerOptions>(configuration.GetSection(SplineFollowerOptions.Position));

        // Never deletes what does not match: nobody is at the PC to configure the addon again, and a
        // mismatch such as a minimized window is gone the next time.
        if (!services.AddWoWProcess(log, keepConfiguration: true, out bool isForever, out string problem))
        {
            refusal = problem;
            return null;
        }

        if (isForever)
        {
            refusal = "WoW: Forever is running: Forever runs through a route assignment";
            return null;
        }

        services.AddCoreBase(log);
        services.AddCoreNormal(log);
        refusal = string.Empty;
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    // HeadlessServer's InitState: flush, then wait until the addon's queued readers stop changing.
    private void InitState(ServiceProvider composed)
    {
        IAddonReader addonReader = composed.GetRequiredService<IAddonReader>();
        ActionBarCostReader actionBarCostReader = composed.GetRequiredService<ActionBarCostReader>();
        SpellBookReader spellBookReader = composed.GetRequiredService<SpellBookReader>();
        BagReader bagReader = composed.GetRequiredService<BagReader>();
        KeyBindingsReader keyBindingsReader = composed.GetRequiredService<KeyBindingsReader>();
        WowProcessInput wowInput = composed.GetRequiredService<WowProcessInput>();
        Wait wait = composed.GetRequiredService<Wait>();

        addonReader.FullReset();
        wowInput.PressFlushKey();

        int actionbarCost;
        int spellBook;
        int bag;
        int keyBindings;

        long startTime = GetTimestamp();
        do
        {
            actionbarCost = actionBarCostReader.Count;
            spellBook = spellBookReader.Count;
            bag = bagReader.BagItems.Count;
            keyBindings = keyBindingsReader.Count;

            for (int i = 0; i < CellUpdateTicks; i++)
                wait.Update();

            if (actionbarCost != actionBarCostReader.Count ||
                spellBook != spellBookReader.Count ||
                bag != bagReader.BagItems.Count ||
                keyBindings != keyBindingsReader.Count)
            {
                LogInitStateStatus(logger, actionbarCost, spellBook, bag, keyBindings);
            }
        } while (
            actionbarCost != actionBarCostReader.Count ||
            spellBook != spellBookReader.Count ||
            bag != bagReader.BagItems.Count ||
            keyBindings != keyBindingsReader.Count);

        if (logger.IsEnabled(LogLevel.Information))
        {
            float elapsed = (float)GetElapsedTime(startTime).TotalSeconds;
            LogInitStateEnd(logger, elapsed);
        }
    }

    private sealed record Readers(IBotController Controller, PlayerReader Player, AddonBits Bits, SessionStat Session);

    #region Logging

    [LoggerMessage(
        EventId = 4000,
        Level = LogLevel.Information,
        Message = "Actionbar: {actionbar,3} | SpellBook: {spellBook,3} | Bag: {bag,3} | Bindings: {keyBindings,3}")]
    static partial void LogInitStateStatus(ILogger logger, int actionbar, int spellbook, int bag, int keyBindings);

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Information,
        Message = "InitState {elapsedSec}sec")]
    static partial void LogInitStateEnd(ILogger logger, float elapsedSec);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Classic bot cannot start: {Reason}")]
    static partial void LogRefused(ILogger logger, string reason);

    #endregion
}
