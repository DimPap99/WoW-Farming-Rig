using Contracts.Pathing;

using PathingService.Forever;
using PathingService.Hosting;
using PathingService.Sync;

using ServiceClients;
using ServiceClients.Pathing;
using ServiceClients.Sync;

using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>The sync sets agents mirror: what each lists, the bytes it serves, and the names it refuses.</summary>
public sealed class SyncApiTests
{
    private const string Lf = "[\n  { \"X\": 1.5, \"Y\": 2.5, \"Z\": 0 }\n]\n";
    private const string Crlf = "[\r\n  { \"X\": 1.5, \"Y\": 2.5, \"Z\": 0 }\r\n]\r\n";
    private const string Spaced = "_pack/Un'Goro Crater/12-14 west side.json";

    private const string Route = """
        {
          // Comments are part of the file: they come back as written.
          "Name": "Trip",
          "UIMapId": 1413,
          "Steps": [ { "Travel": { "To": [51.9, 29.9] } } ]
        }
        """;

    [Fact]
    public async Task Each_set_lists_its_files_with_their_size_and_hash()
    {
        using TempDataRoot data = SyncData();
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<SyncManifest> paths = await client.GetSyncManifestAsync(SyncSets.Paths, null, TestContext.Current.CancellationToken);
        ServiceResult<SyncManifest> routes = await client.GetSyncManifestAsync(SyncSets.ForeverRoutes, null, TestContext.Current.CancellationToken);
        ServiceResult<SyncManifest> zones = await client.GetSyncManifestAsync(SyncSets.CostZones, "precata", TestContext.Current.CancellationToken);
        ServiceResult<SyncManifest> none = await client.GetSyncManifestAsync(SyncSets.CostZones, "forever", TestContext.Current.CancellationToken);

        Assert.True(paths.Ok, paths.Error);
        // The route folder is a set of its own, and a screenshot is not a data file.
        Assert.Equal([Spaced, "a.json", "sub/b.json"], paths.Value.Files.Select(file => file.Name));
        Assert.Null(paths.Value.Era);
        SyncFile b = paths.Value.Files[2];
        Assert.Equal(Encoding.UTF8.GetByteCount(Crlf), b.Bytes);
        Assert.Equal(SyncHash.Of(Encoding.UTF8.GetBytes(Lf)), b.Sha256);

        Assert.True(routes.Ok, routes.Error);
        Assert.Equal(["trip.json"], routes.Value.Files.Select(file => file.Name));

        Assert.True(zones.Ok, zones.Error);
        Assert.Equal("precata", zones.Value.Era);
        Assert.Equal(["Azeroth/1429.json", "Azeroth/dangerzone/1429.json", "Kalimdor.json"], zones.Value.Files.Select(file => file.Name));

        // A known era with nothing authored yet.
        Assert.True(none.Ok, none.Error);
        Assert.Empty(none.Value.Files);
    }

    [Fact]
    public async Task A_file_comes_back_exactly_as_stored()
    {
        using TempDataRoot data = SyncData();
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<byte[]> crlf = await client.GetSyncFileAsync(SyncSets.Paths, "sub/b.json", null, TestContext.Current.CancellationToken);
        ServiceResult<byte[]> spaced = await client.GetSyncFileAsync(SyncSets.Paths, Spaced, null, TestContext.Current.CancellationToken);
        ServiceResult<byte[]> route = await client.GetSyncFileAsync(SyncSets.ForeverRoutes, "trip.json", null, TestContext.Current.CancellationToken);
        ServiceResult<byte[]> danger = await client.GetSyncFileAsync(SyncSets.CostZones, "Azeroth/dangerzone/1429.json", "precata",
            TestContext.Current.CancellationToken);

        Assert.True(crlf.Ok, crlf.Error);
        Assert.Equal(Encoding.UTF8.GetBytes(Crlf), crlf.Value);
        Assert.True(spaced.Ok, spaced.Error);
        Assert.Equal(File.ReadAllBytes(Path.Combine(data.Root, "path", Spaced)), spaced.Value);
        Assert.True(route.Ok, route.Error);
        Assert.Equal(Encoding.UTF8.GetBytes(Route), route.Value);
        Assert.True(danger.Ok, danger.Error);
        Assert.Equal(File.ReadAllBytes(Path.Combine(data.Root, "road", "precata", "Azeroth", "dangerzone", "1429.json")), danger.Value);
    }

    [Fact]
    public async Task What_a_request_cannot_name_is_refused_and_what_is_not_there_is_not_found()
    {
        using TempDataRoot data = SyncData();
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<SyncManifest> unknown = await client.GetSyncManifestAsync("tiles", null, TestContext.Current.CancellationToken);
        ServiceResult<SyncManifest> noEra = await client.GetSyncManifestAsync(SyncSets.CostZones, null, TestContext.Current.CancellationToken);
        ServiceResult<SyncManifest> badEra = await client.GetSyncManifestAsync(SyncSets.CostZones, "retail", TestContext.Current.CancellationToken);
        ServiceResult<SyncManifest> eraOnPaths = await client.GetSyncManifestAsync(SyncSets.Paths, "precata", TestContext.Current.CancellationToken);
        ServiceResult<byte[]> route = await client.GetSyncFileAsync(SyncSets.Paths, "_forever/trip.json", null, TestContext.Current.CancellationToken);
        ServiceResult<byte[]> missing = await client.GetSyncFileAsync(SyncSets.Paths, "missing.json", null, TestContext.Current.CancellationToken);

        Assert.Equal(400, unknown.Status);
        Assert.Contains("not a sync set", unknown.Error);
        Assert.Equal(400, noEra.Status);
        Assert.Contains("by era", noEra.Error);
        Assert.Equal(400, badEra.Status);
        Assert.Equal(400, eraOnPaths.Status);
        Assert.Contains("has no era", eraOnPaths.Error);
        Assert.Equal(400, route.Status);
        Assert.Equal(404, missing.Status);
    }

    [Theory]
    [InlineData(SyncSets.Paths, null, "../escape.json")]
    [InlineData(SyncSets.Paths, null, "/rooted.json")]
    [InlineData(SyncSets.Paths, null, "sub\\b.json")]
    [InlineData(SyncSets.Paths, null, "a.jpg")]
    [InlineData(SyncSets.Paths, null, "_forever/trip.json")]
    [InlineData(SyncSets.ForeverRoutes, null, "sub/trip.json")]
    [InlineData(SyncSets.ForeverRoutes, null, "../a.json")]
    [InlineData(SyncSets.CostZones, "precata", "../../path/a.json")]
    [InlineData(SyncSets.CostZones, "precata", "Azeroth\\1429.json")]
    [InlineData(SyncSets.CostZones, "../precata", "Kalimdor.json")]
    public void A_name_that_would_leave_its_set_is_refused(string set, string? era, string name)
    {
        using TempDataRoot data = SyncData();
        SyncLibrary library = new(data.Config(), new ForeverRouteLibrary(data.Config()));

        StoreResult<string> found = library.Find(set, name, era);

        Assert.Equal(StoreOutcome.Invalid, found.Outcome);
        Assert.NotEmpty(found.Error!);
    }

    [Theory]
    [InlineData("som")]
    [InlineData("vanilla")]
    [InlineData("tbc")]
    [InlineData("wrath")]
    [InlineData("legacy_wrath")]
    [InlineData("cata")]
    [InlineData("legacy_cata")]
    [InlineData("mop")]
    [InlineData("legacy_mop")]
    [InlineData("forever")]
    public void Every_known_clients_era_can_be_synced(string client)
    {
        Assert.Contains(DataConfig.ClientEra(client), DataConfig.Eras);
    }

    [Fact]
    public void Line_endings_do_not_change_the_hash()
    {
        // SHA-256 of nothing and of "abc", in lower-case hex: the manifest's format.
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", SyncHash.Of([]));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", SyncHash.Of("abc"u8));

        Assert.Equal(SyncHash.Of("a\nb\n"u8), SyncHash.Of("a\r\nb\r\n"u8));
        Assert.Equal(SyncHash.Of("a\n\nb"u8), SyncHash.Of("a\r\n\r\nb"u8));
        // Only a CR before an LF goes: a lone CR is part of the text.
        Assert.NotEqual(SyncHash.Of("a\nb"u8), SyncHash.Of("a\rb"u8));
        Assert.NotEqual(SyncHash.Of("a\n"u8), SyncHash.Of("a\r"u8));
    }

    // Recorded paths (one with CRLF line endings, one in folders with spaces and an apostrophe), a
    // screenshot, a Forever route and precata's cost zones: a continent folder, its danger zones and
    // an old single-file continent.
    private static TempDataRoot SyncData()
    {
        TempDataRoot data = new(withMapData: true);
        Write(data, "path/a.json", Lf);
        Write(data, "path/sub/b.json", Crlf);
        Write(data, "path/" + Spaced, Lf);
        Write(data, "path/a.jpg", "not json");
        Write(data, "path/_forever/trip.json", Route);
        Write(data, "road/precata/Azeroth/1429.json", """{ "Roads": [] }""");
        Write(data, "road/precata/Azeroth/dangerzone/1429.json", """{ "Circles": [], "Rectangles": [] }""");
        Write(data, "road/precata/Kalimdor.json", """{ "Roads": [] }""");
        return data;
    }

    private static void Write(TempDataRoot data, string relative, string text)
    {
        string file = Path.Combine(data.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }
}
