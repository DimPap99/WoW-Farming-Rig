using BotAgent.Runtime;
using BotAgent.Sync;

using Contracts.Bots;

using Microsoft.Extensions.Logging.Abstractions;

using ServiceClients.Pathing;

using ServiceTests.Api;

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Agent;

/// <summary>
/// The agent's data sync against the Pathing Service in memory. The service's data root and the
/// agent's are two temporary folders, as the central machine's and a bot PC's are.
/// </summary>
public sealed class DataSyncTests : IDisposable
{
    private const string Route = "trip.json";
    private const string RouteFile = "path/_forever/" + Route;

    private const string Trip = """
        {
          // To the Crossroads.
          "Name": "Trip",
          "UIMapId": 1413,
          "Steps": [ { "Travel": { "To": [51.9, 29.9] } } ]
        }
        """;

    private const string Points = "[ { \"X\": 1.5, \"Y\": 2.5, \"Z\": 0 } ]";
    private const string Roads = """{ "Roads": [] }""";

    private static readonly AgentGame Forever = new("forever", "forever", 1, "1.60.1.70009");
    private static readonly AgentGame Classic = new("som", "precata", 1, "1.15.7.61582");

    private readonly TempDataRoot central = new(withMapData: true);
    private readonly TempDataRoot here = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Backups => Path.Combine(here.Root, DataConfig.SyncFolder, DataSync.BackupFolder);

    public void Dispose()
    {
        central.Dispose();
        here.Dispose();
    }

    [Fact]
    public async Task A_Forever_run_pulls_its_route_and_its_eras_cost_zones_byte_for_byte()
    {
        Write(central, RouteFile, Trip);
        Write(central, "road/forever/Kalimdor/1413.json", Roads);
        Write(central, "road/forever/Kalimdor/dangerzone/1413.json", """{ "Circles": [], "Rectangles": [] }""");
        Write(central, "road/precata/Azeroth/1429.json", Roads);
        using PathingServiceFactory service = new(central.Root);

        RunPreparation prepared = await Sync(service).PrepareAsync(BotAssignment.ForForever(Route, 5), Token);

        Assert.True(prepared.Proceed, prepared.Message);
        Assert.Equal("Pulled 3 files from the Pathing Service, 0 up to date.", prepared.Message);
        Assert.Equal(Bytes(central, RouteFile), Bytes(here, RouteFile));
        Assert.Equal(Bytes(central, "road/forever/Kalimdor/dangerzone/1413.json"), Bytes(here, "road/forever/Kalimdor/dangerzone/1413.json"));
        // Another era's zones are not this run's.
        Assert.False(File.Exists(Path.Combine(here.Root, "road/precata/Azeroth/1429.json")));
        Assert.Equal(["costzones/forever/Kalimdor/1413.json", "costzones/forever/Kalimdor/dangerzone/1413.json", "forever-routes/trip.json"],
            Synced());
    }

    [Fact]
    public async Task On_a_file_system_that_ignores_case_two_names_of_one_file_are_not_pulled()
    {
        // Made on a Linux service before such names were refused (known issue 71).
        Write(central, RouteFile, Trip);
        Write(central, "path/_forever/Trip.json", Trip.Replace("\"Trip\"", "\"The other trip\""));
        Write(central, "road/forever/Kalimdor/1413.json", Roads);
        Write(central, "road/forever/kalimdor/1413.json", Roads);
        Write(here, RouteFile, Trip);
        using PathingServiceFactory service = new(central.Root);

        RunPreparation windows = await Sync(service, fileNames: StringComparer.OrdinalIgnoreCase)
            .PrepareAsync(BotAssignment.ForForever(Route, 5), Token);

        Assert.False(windows.Proceed);
        Assert.StartsWith($"The route {Route} is two files on the Pathing Service (Trip.json and trip.json) and one on this PC", windows.Message);
        Assert.Contains("Not synced, two files on the Pathing Service that are one on this PC: Kalimdor/1413.json and kalimdor/1413.json", windows.Message);
        Assert.Equal(Encoding.UTF8.GetBytes(Trip), Bytes(here, RouteFile));
        Assert.False(Directory.Exists(Path.Combine(here.Root, "road/forever/Kalimdor")));

        // Where names keep their case, each is a file of its own and pulled.
        RunPreparation linux = await Sync(service).PrepareAsync(BotAssignment.ForForever(Route, 5), Token);
        Assert.True(linux.Proceed, linux.Message);
    }

    // Taken as they came, with no check: a file changed since the manifest, cut short, or not the file
    // at all was written over this PC's copy (known issue 77).
    [Fact]
    public async Task Bytes_other_than_the_manifest_listed_are_not_written()
    {
        Write(central, RouteFile, Trip);
        Write(here, RouteFile, Trip.Replace("\"Trip\"", "\"Trip, this PC's\""));
        using PathingServiceFactory service = new(central.Root);
        Tamper cut = new((path, text) => path.Contains("/files/", StringComparison.Ordinal) ? text[..(text.Length / 2)] : text);

        RunPreparation prepared = await Sync(service, between: cut).PrepareAsync(BotAssignment.ForForever(Route, 5), Token);

        Assert.True(prepared.Proceed, prepared.Message);
        Assert.Contains("Not updated, this PC's copy used: forever-routes/trip.json (it came back other than listed)", prepared.Message);
        Assert.Equal(Encoding.UTF8.GetBytes(Trip.Replace("\"Trip\"", "\"Trip, this PC's\"")), Bytes(here, RouteFile));
    }

    [Fact]
    public async Task A_manifest_with_an_empty_entry_is_a_refused_set_and_the_run_uses_this_PCs_copies()
    {
        Write(central, RouteFile, Trip);
        Write(here, RouteFile, Trip);
        using PathingServiceFactory service = new(central.Root);
        Tamper empty = new((path, text) => path.EndsWith("/forever-routes", StringComparison.Ordinal)
            ? text.Replace("\"files\":[", "\"files\":[null,", StringComparison.Ordinal)
            : text);

        RunPreparation prepared = await Sync(service, between: empty).PrepareAsync(BotAssignment.ForForever(Route, 5), Token);

        Assert.True(prepared.Proceed, prepared.Message);
        Assert.Contains("The forever-routes set was refused (it answered a manifest this agent cannot read)", prepared.Message);
    }

    [Fact]
    public async Task A_copy_that_matches_is_left_alone_whatever_its_line_endings()
    {
        Write(central, RouteFile, Trip.ReplaceLineEndings("\n"));
        Write(here, RouteFile, Trip.ReplaceLineEndings("\r\n"));
        using PathingServiceFactory service = new(central.Root);

        RunPreparation prepared = await Sync(service).PrepareAsync(BotAssignment.ForForever(Route, 5), Token);

        Assert.True(prepared.Proceed, prepared.Message);
        Assert.Equal("Up to date with the Pathing Service (1 file).", prepared.Message);
        Assert.Equal(Encoding.UTF8.GetBytes(Trip.ReplaceLineEndings("\r\n")), Bytes(here, RouteFile));
        Assert.Equal(["forever-routes/trip.json"], Synced());
        Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public async Task A_file_changed_centrally_replaces_the_agents_own_copy_without_keeping_it()
    {
        Write(central, RouteFile, Trip);
        using PathingServiceFactory service = new(central.Root);
        DataSync sync = Sync(service);
        await sync.PrepareAsync(BotAssignment.ForForever(Route, 5), Token);

        Write(central, RouteFile, Trip.Replace("\"Trip\"", "\"Trip, changed\""));
        RunPreparation prepared = await sync.PrepareAsync(BotAssignment.ForForever(Route, 5), Token);

        Assert.Equal("Pulled 1 file from the Pathing Service, 0 up to date.", prepared.Message);
        Assert.Equal(Bytes(central, RouteFile), Bytes(here, RouteFile));
        Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public async Task A_copy_changed_on_this_PC_is_kept_aside_before_it_is_replaced()
    {
        Write(central, RouteFile, Trip);
        using PathingServiceFactory service = new(central.Root);
        DataSync sync = Sync(service);
        await sync.PrepareAsync(BotAssignment.ForForever(Route, 5), Token);
        string edited = Trip.Replace("\"Trip\"", "\"Trip, edited on this PC\"");
        Write(here, RouteFile, edited);

        RunPreparation prepared = await sync.PrepareAsync(BotAssignment.ForForever(Route, 5), Token);

        Assert.True(prepared.Proceed, prepared.Message);
        Assert.StartsWith("Pulled 1 file from the Pathing Service, 0 up to date. Local changes kept aside in ", prepared.Message);
        Assert.EndsWith(": forever-routes/trip.json.", prepared.Message);
        string kept = Assert.Single(Directory.GetFiles(Backups, "*", SearchOption.AllDirectories));
        Assert.EndsWith(Path.Combine("forever-routes", Route), kept);
        Assert.Equal(edited, File.ReadAllText(kept));
        Assert.Equal(Bytes(central, RouteFile), Bytes(here, RouteFile));
    }

    [Fact]
    public async Task A_copy_that_was_never_synced_and_differs_is_kept_aside_too()
    {
        // The first sync on a PC whose checkout is older than the service.
        Write(central, RouteFile, Trip);
        Write(here, RouteFile, Trip.Replace("\"Trip\"", "\"Trip, as checked out\""));
        using PathingServiceFactory service = new(central.Root);

        RunPreparation prepared = await Sync(service).PrepareAsync(BotAssignment.ForForever(Route, 5), Token);

        Assert.Contains("Local changes kept aside in ", prepared.Message);
        Assert.Contains("as checked out", File.ReadAllText(Assert.Single(Directory.GetFiles(Backups, "*", SearchOption.AllDirectories))));
        Assert.Equal(Bytes(central, RouteFile), Bytes(here, RouteFile));
    }

    [Fact]
    public async Task Nothing_is_ever_deleted()
    {
        Write(central, RouteFile, Trip);
        Write(central, "road/forever/Kalimdor/1413.json", Roads);
        Write(here, "path/_forever/mine.json", Trip);
        Write(here, "road/forever/Kalimdor/9999.json", Roads);
        using PathingServiceFactory service = new(central.Root);

        RunPreparation prepared = await Sync(service).PrepareAsync(BotAssignment.ForForever(Route, 5), Token);

        Assert.Equal("Pulled 2 files from the Pathing Service, 0 up to date.", prepared.Message);
        Assert.True(File.Exists(Path.Combine(here.Root, "path/_forever/mine.json")));
        Assert.True(File.Exists(Path.Combine(here.Root, "road/forever/Kalimdor/9999.json")));
    }

    [Fact]
    public async Task A_Classic_run_pulls_the_paths_its_profile_names_and_nothing_else()
    {
        // The profile is this PC's own: profiles are committed data, not synced.
        Write(here, "class/Hunter_test.json", ProfilePathsTests.Profile);
        foreach (string name in (string[])["legacy.json", "_pack/1-20/Human/a b.json", "_p2/lower.json", "grouped.json", "vendor.json",
                     "override.json", "unrelated.json"])
        {
            Write(central, "path/" + name, Points);
        }

        Write(central, "path/group.json", ProfilePathsTests.Group);
        Write(central, "road/precata/Azeroth/1429.json", Roads);
        using PathingServiceFactory service = new(central.Root);

        RunPreparation prepared = await Sync(service, Classic).PrepareAsync(BotAssignment.ForClassic("Hunter_test.json", "override.json"), Token);

        Assert.True(prepared.Proceed, prepared.Message);
        Assert.Equal(["_p2/lower.json", "_pack/1-20/Human/a b.json", "group.json", "grouped.json", "legacy.json", "override.json", "vendor.json"],
            Files(here, "path"));
        Assert.Equal(Bytes(central, "road/precata/Azeroth/1429.json"), Bytes(here, "road/precata/Azeroth/1429.json"));
        Assert.StartsWith("Pulled 8 files from the Pathing Service, 0 up to date.", prepared.Message);
        Assert.EndsWith("Not synced, not a library name: C:\\elsewhere\\abs.json.", prepared.Message);
    }

    [Fact]
    public async Task With_the_service_down_a_run_uses_this_PCs_copies_and_one_missing_its_route_is_refused()
    {
        Write(here, RouteFile, Trip);
        DataSync sync = new(new PathingClient(new HttpClient { BaseAddress = ClosedPort.NewUrl() }), here.Config(),
            new FakeProbe { Game = Forever }, TimeProvider.System, NullLogger<DataSync>.Instance);

        RunPreparation local = await sync.PrepareAsync(BotAssignment.ForForever(Route, 5), Token);
        RunPreparation missing = await sync.PrepareAsync(BotAssignment.ForForever("missing.json", 5), Token);

        Assert.True(local.Proceed, local.Message);
        Assert.StartsWith("The pathing service could not be reached", local.Message);
        Assert.EndsWith("; the run uses this PC's copies.", local.Message);
        Assert.False(missing.Proceed);
        Assert.StartsWith("The route missing.json is not on this PC, and the pathing service could not be reached", missing.Message);
    }

    [Fact]
    public async Task With_no_service_set_a_run_uses_this_PCs_copies()
    {
        Write(here, RouteFile, Trip);
        DataSync sync = new(null, here.Config(), new FakeProbe { Game = Forever }, TimeProvider.System, NullLogger<DataSync>.Instance);

        RunPreparation local = await sync.PrepareAsync(BotAssignment.ForForever(Route, 5), Token);
        RunPreparation missing = await sync.PrepareAsync(BotAssignment.ForForever("missing.json", 5), Token);

        Assert.True(local.Proceed, local.Message);
        Assert.Equal("No Pathing Service is set (PathingService:Url): the run uses this PC's copies.", local.Message);
        Assert.False(missing.Proceed);
        Assert.Equal("The route missing.json is not on this PC, and no Pathing Service is set (PathingService:Url).", missing.Message);
    }

    [Fact]
    public async Task A_route_only_this_PC_has_is_used_and_one_nobody_has_is_refused()
    {
        Write(here, "path/_forever/mine.json", Trip);
        using PathingServiceFactory service = new(central.Root);
        DataSync sync = Sync(service);

        RunPreparation mine = await sync.PrepareAsync(BotAssignment.ForForever("mine.json", 5), Token);
        RunPreparation nobody = await sync.PrepareAsync(BotAssignment.ForForever("missing.json", 5), Token);

        Assert.True(mine.Proceed, mine.Message);
        Assert.Equal("Nothing to pull from the Pathing Service. Not on the Pathing Service, used as this PC has them: forever-routes/mine.json.",
            mine.Message);
        Assert.False(nobody.Proceed);
        Assert.Equal("The route missing.json is not on this PC, and the Pathing Service does not have it either.", nobody.Message);
    }

    [Fact]
    public async Task A_run_pulls_its_data_then_runs()
    {
        Write(central, RouteFile, Trip);
        using PathingServiceFactory service = new(central.Root);
        FakeRuntimeFactory runtimes = new();
        await using AgentUnderTest agent = await AgentUnderTest.StartAsync(here.Root, null, runtimes, preparation: Sync(service));

        Assert.True(agent.Host.Start(BotAssignment.ForForever(Route, 5)).Accepted);

        await Eventually.True(() => agent.Host.Read().State == BotRunState.Running);
        Assert.Equal(Bytes(central, RouteFile), Bytes(here, RouteFile));
    }

    [Fact]
    public async Task A_stop_while_a_run_syncs_is_a_stop()
    {
        StalledPreparation preparation = new();
        FakeRuntimeFactory runtimes = new();
        await using AgentUnderTest agent = await AgentUnderTest.StartAsync(here.Root, null, runtimes, preparation: preparation);
        TaskCompletionSource<RunReport> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.Host.RunEnded += report => ended.TrySetResult(report);

        Assert.True(agent.Host.Start(BotAssignment.ForForever(Route, 5)).Accepted);
        await preparation.Entered.WaitAsync(Eventually.Wait, Token);
        Assert.Equal(BotRunState.Syncing, agent.Host.Read().State);
        agent.Host.Stop();
        RunReport report = await ended.Task.WaitAsync(Eventually.Wait, Token);

        Assert.Equal(BotRunOutcome.Stopped, report.Outcome);
        Assert.Equal("stopped before it started", report.Message);
        Assert.Empty(runtimes.Built);
    }

    private DataSync Sync(PathingServiceFactory service, AgentGame? game = null, StringComparer? fileNames = null,
        DelegatingHandler? between = null)
    {
        HttpClient http = between is null ? service.CreateClient() : service.CreateDefaultClient(between);
        return new DataSync(new PathingClient(http), here.Config(), new FakeProbe { Game = game ?? Forever },
            TimeProvider.System, NullLogger<DataSync>.Instance, fileNames);
    }

    // Changes what the service answers on the way: a file's bytes, or a manifest's text.
    private sealed class Tamper(Func<string, string, string> change) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Content = new StringContent(change(request.RequestUri!.AbsolutePath, text), Encoding.UTF8,
                response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
            return response;
        }
    }

    private string[] Synced()
    {
        using JsonDocument state = JsonDocument.Parse(File.ReadAllText(Path.Combine(here.Root, DataConfig.SyncFolder, SyncState.FileName)));
        return [.. state.RootElement.GetProperty("Files").EnumerateObject().Select(file => file.Name)];
    }

    private static string[] Files(TempDataRoot root, string folder)
    {
        string full = Path.Combine(root.Root, folder);
        return [.. Directory.GetFiles(full, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(full, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];
    }

    private static byte[] Bytes(TempDataRoot root, string relative)
    {
        return File.ReadAllBytes(Path.Combine(root.Root, relative));
    }

    private static void Write(TempDataRoot root, string relative, string text)
    {
        string file = Path.Combine(root.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    // A sync that never answers, until the run's stop cancels it.
    private sealed class StalledPreparation : IRunPreparation
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => entered.Task;

        public async Task<RunPreparation> PrepareAsync(BotAssignment assignment, CancellationToken token)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new RunPreparation(true, null);
        }
    }
}
