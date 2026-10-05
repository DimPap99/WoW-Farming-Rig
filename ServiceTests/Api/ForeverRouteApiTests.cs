using Contracts.Pathing;

using PathingService.Forever;

using ServiceClients;
using ServiceClients.Pathing;

using SharedLib.Forever;

using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>The Forever route library end to end, through the client the Control Center and the agent use.</summary>
public sealed class ForeverRouteApiTests
{
    private const string Example = "durotar_example.json";
    private const string Trip = "crossroads_trip.json";

    // A route with comments that document it, as route files carry them.
    private const string Commented = """
        {
          // To the Crossroads: comments survive a save.
          "Name": "Commented trip",
          "UIMapId": 1413,
          "Steps": [
            { "Travel": { "To": [51.9, 29.9], "Pulls": "OnTheWay" } } // the only step
          ]
        }
        """;

    private const string Refused = """{ "Name": "Bad", "UIMapId": 1411, "Steps": [ { "Travel": { "To": [152, 44] } } ] }""";

    [Fact]
    public async Task The_library_lists_each_route_checked_as_a_run_would_load_it()
    {
        using TempDataRoot data = ForeverData();
        File.WriteAllText(Path.Combine(RouteFolder(data), "broken.json"), Refused);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<ForeverRouteInfo[]> listed = await client.ListForeverRoutesAsync(TestContext.Current.CancellationToken);

        Assert.True(listed.Ok, listed.Error);
        Assert.Equal(["broken.json", Trip, Example], [.. listed.Value.Select(route => route.Name)]);
        ForeverRouteInfo broken = listed.Value[0];
        Assert.Equal("broken", broken.Title);
        Assert.Empty(broken.Steps);
        Assert.Contains("Bad, step 1: the map point", broken.Problem);
        ForeverRouteInfo example = listed.Value[2];
        Assert.Null(example.Problem);
        Assert.Equal("Durotar example: south of Razor Hill", example.Title);
        Assert.Equal(3, example.Steps.Length);
        Assert.StartsWith("Travel to 52.5, 44.5 on map 1411", example.Steps[0]);
        Assert.True(example.Bytes > 0);
        Assert.NotNull(example.ModifiedUtc);
    }

    [Fact]
    public async Task A_saved_route_keeps_its_comments_byte_for_byte()
    {
        using TempDataRoot data = ForeverData();
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult saved = await client.SaveForeverRouteAsync("commented.json", Commented, TestContext.Current.CancellationToken);
        ServiceResult<ForeverRouteDocument> read = await client.GetForeverRouteAsync("commented.json", TestContext.Current.CancellationToken);

        Assert.True(saved.Ok, saved.Error);
        Assert.Equal(Encoding.UTF8.GetBytes(Commented), File.ReadAllBytes(Path.Combine(RouteFolder(data), "commented.json")));
        Assert.True(read.Ok, read.Error);
        Assert.Equal(Commented, read.Value.Text);
        Assert.Equal("Commented trip", read.Value.Info.Title);
        Assert.Null(read.Value.Info.Problem);
    }

    [Fact]
    public async Task A_route_a_run_would_refuse_is_never_written()
    {
        using TempDataRoot data = ForeverData();
        string trip = Path.Combine(RouteFolder(data), Trip);
        byte[] before = File.ReadAllBytes(trip);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult replaced = await client.SaveForeverRouteAsync(Trip, Refused, TestContext.Current.CancellationToken);
        ServiceResult created = await client.SaveForeverRouteAsync("new.json", Refused, TestContext.Current.CancellationToken);
        ServiceResult empty = await client.SaveForeverRouteAsync("new.json", " ", TestContext.Current.CancellationToken);

        Assert.Equal(400, replaced.Status);
        Assert.Contains("step 1: the map point", replaced.Error);
        Assert.Equal(before, File.ReadAllBytes(trip));
        Assert.Equal(400, created.Status);
        Assert.Equal(400, empty.Status);
        Assert.False(File.Exists(Path.Combine(RouteFolder(data), "new.json")));
    }

    [Fact]
    public async Task A_draft_is_checked_without_being_saved()
    {
        using TempDataRoot data = ForeverData();
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<ForeverRouteInfo> good = await client.CheckForeverRouteAsync(new ForeverRouteDraft("draft.json", Commented),
            TestContext.Current.CancellationToken);
        ServiceResult<ForeverRouteInfo> bad = await client.CheckForeverRouteAsync(new ForeverRouteDraft("draft.json", Refused),
            TestContext.Current.CancellationToken);
        ServiceResult<ForeverRouteInfo> badName = await client.CheckForeverRouteAsync(new ForeverRouteDraft("draft.txt", Commented),
            TestContext.Current.CancellationToken);

        Assert.True(good.Ok, good.Error);
        Assert.Null(good.Value.Problem);
        Assert.Equal(["Travel to 51.9, 29.9 on map 1413"], good.Value.Steps);
        Assert.Equal(0, good.Value.Bytes);
        Assert.Null(good.Value.ModifiedUtc);
        Assert.True(bad.Ok, bad.Error);
        Assert.Contains("step 1", bad.Value.Problem);
        Assert.Equal(400, badName.Status);
        Assert.False(File.Exists(Path.Combine(RouteFolder(data), "draft.json")));
    }

    [Fact]
    public async Task A_route_is_deleted_once()
    {
        using TempDataRoot data = ForeverData();
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult deleted = await client.DeleteForeverRouteAsync(Trip, TestContext.Current.CancellationToken);
        ServiceResult again = await client.DeleteForeverRouteAsync(Trip, TestContext.Current.CancellationToken);
        ServiceResult<ForeverRouteDocument> gone = await client.GetForeverRouteAsync(Trip, TestContext.Current.CancellationToken);

        Assert.True(deleted.Ok, deleted.Error);
        Assert.Equal(404, again.Status);
        Assert.Equal(404, gone.Status);
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("sub/route.json")]
    [InlineData("folder\\route.json")]
    [InlineData("route.txt")]
    [InlineData(".json")]
    [InlineData(" padded.json")]
    [InlineData("")]
    public void Route_names_are_plain_file_names_in_the_route_folder(string name)
    {
        using TempDataRoot data = ForeverData();
        ForeverRouteLibrary library = new(data.Config());

        Assert.False(library.TryResolve(name, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task A_bad_name_is_refused_by_every_call()
    {
        using TempDataRoot data = ForeverData();
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<ForeverRouteDocument> get = await client.GetForeverRouteAsync("route.txt", TestContext.Current.CancellationToken);
        ServiceResult put = await client.SaveForeverRouteAsync("route.txt", Commented, TestContext.Current.CancellationToken);
        ServiceResult delete = await client.DeleteForeverRouteAsync("route.txt", TestContext.Current.CancellationToken);

        Assert.Equal(400, get.Status);
        Assert.Contains("must end in .json", get.Error);
        Assert.Equal(400, put.Status);
        Assert.Equal(400, delete.Status);
    }

    [Fact]
    public async Task Without_Forever_map_data_routes_say_why_they_cannot_be_checked()
    {
        using TempDataRoot data = new(withMapData: true);
        data.CopyRepoFile(Path.Combine("path", ForeverRouteFile.Folder, Trip));
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<ForeverRouteInfo[]> listed = await client.ListForeverRoutesAsync(TestContext.Current.CancellationToken);
        ServiceResult saved = await client.SaveForeverRouteAsync("commented.json", Commented, TestContext.Current.CancellationToken);

        Assert.True(listed.Ok, listed.Error);
        Assert.Contains("map data", Assert.Single(listed.Value).Problem);
        Assert.Equal(500, saved.Status);
        Assert.Contains("map data", saved.Error);
    }

    // The service's own client's map areas (som), Forever's, and the two shipped route files.
    private static TempDataRoot ForeverData()
    {
        TempDataRoot data = new(withMapData: true);
        data.CopyRepoFile(Path.Combine("dbc", "forever", "WorldMapArea.json"));
        data.CopyRepoFile(Path.Combine("path", ForeverRouteFile.Folder, Example));
        data.CopyRepoFile(Path.Combine("path", ForeverRouteFile.Folder, Trip));
        return data;
    }

    private static string RouteFolder(TempDataRoot data)
    {
        return Path.Combine(data.Config().Path, ForeverRouteFile.Folder);
    }
}
