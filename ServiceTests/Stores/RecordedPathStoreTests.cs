using Contracts;
using Contracts.Pathing;

using PathingService.Hosting;
using PathingService.Paths;

using ServiceHosting;

using SharedLib.Converters;
using SharedLib.Forever;

using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

using Xunit;

namespace ServiceTests.Stores;

public sealed class RecordedPathStoreTests
{
    private static readonly Point3[] Points = [new(48.564f, 59.133f, 0), new(48.546f, 58.525f, 0), new(48.456f, 57.866f, 12.5f)];

    // A PathSettings group that every Human 1-10 profile names; it shares the folder with the paths.
    private const string Group = "1-8_Human_Northshire.json";

    // Frontend/Controllers/PathController.SavePath: System.Text.Json with an upper-case Vector3Converter.
    private static readonly JsonSerializerOptions ControllerOptions = new() { Converters = { new Vector3Converter(true) } };

    [Fact]
    public void A_saved_path_is_byte_for_byte_what_PathController_wrote()
    {
        using TempDataRoot data = new();
        RecordedPathStore store = new(data.Config());

        Assert.Equal(ApiOutcome.Done, store.Save("10_Gnome.json", Points).Outcome);

        string expected = JsonSerializer.Serialize(Points.Select(p => new Vector3(p.X, p.Y, p.Z)).ToArray(), ControllerOptions);
        Assert.Equal(expected, File.ReadAllText(Path.Combine(data.Config().Path, "10_Gnome.json")));
    }

    [Fact]
    public void A_path_reads_back_and_lists_with_its_folder()
    {
        using TempDataRoot data = new();
        RecordedPathStore store = new(data.Config());
        store.Save("_herb/Elwynn.json", Points);
        store.Save("Loch.json", Points);
        File.WriteAllText(Path.Combine(data.Config().Path, "Loch.jpg"), "image");

        ApiResult<RecordedPath> read = store.Get("_herb/Elwynn.json");

        Assert.Equal(ApiOutcome.Done, read.Outcome);
        Assert.Equal(Points, read.Value!.Points);
        string[] names = ["Loch.json", "_herb/Elwynn.json"];
        Assert.Equal(names, store.List(null).Select(p => p.Name).Order(System.StringComparer.Ordinal));
        Assert.Equal("Loch.jpg", store.List("loch").Single().Screenshot);
    }

    [Fact]
    public void A_file_without_heights_reads_like_the_bot_reads_it()
    {
        using TempDataRoot data = new();
        Directory.CreateDirectory(data.Config().Path);
        File.WriteAllText(Path.Combine(data.Config().Path, "old.json"), """[{"X":48.564,"Y":59.133},{"x":1.5,"y":2.5}]""");

        ApiResult<RecordedPath> read = new RecordedPathStore(data.Config()).Get("old.json");

        Assert.Equal([new Point3(48.564f, 59.133f, 0), new Point3(1.5f, 2.5f, 0)], read.Value!.Points);
    }

    [Fact]
    public void Forever_route_files_are_not_recorded_paths()
    {
        using TempDataRoot data = new();
        string forever = Path.Combine(data.Config().Path, ForeverRouteFile.Folder);
        Directory.CreateDirectory(forever);
        File.WriteAllText(Path.Combine(forever, "trip.json"), "{}");
        RecordedPathStore store = new(data.Config());

        Assert.Empty(store.List(null));
        Assert.Equal(ApiOutcome.Invalid, store.Get("_forever/trip.json").Outcome);
        Assert.Equal(ApiOutcome.Invalid, store.Save("_forever/trip.json", Points).Outcome);
    }

    [Fact]
    public void A_class_profiles_path_group_is_left_alone()
    {
        using TempDataRoot data = new();
        string group = data.CopyRepoFile(Path.Combine("path", Group));
        byte[] before = File.ReadAllBytes(group);
        RecordedPathStore store = new(data.Config());
        store.Save("Loch.json", Points);

        ApiResult<RecordedPath> read = store.Get(Group);
        ApiResult saved = store.Save(Group, Points);
        ApiResult deleted = store.Delete(Group);

        Assert.Equal(["Loch.json"], store.List(null).Select(p => p.Name));
        Assert.Equal(ApiOutcome.Invalid, read.Outcome);
        Assert.Contains("\"Requirements\"", read.Error);
        Assert.Equal(ApiOutcome.Invalid, saved.Outcome);
        Assert.Equal(ApiOutcome.Invalid, deleted.Outcome);
        Assert.Equal(before, File.ReadAllBytes(group));
        Assert.Equal(Points, store.Get("Loch.json").Value!.Points);
    }

    [Fact]
    public void The_repositorys_library_is_every_recorded_path_and_no_group()
    {
        DataConfig repo = TestData.RepoConfig();
        RecordedPathStore store = new(repo);

        string[] listed = [.. store.List(null).Select(p => p.Name)];
        string[] left = [.. Directory.EnumerateFiles(repo.Path, "*.json", SearchOption.AllDirectories)
            .Select(file => PathName.Relative(repo.Path, file))
            .Where(name => !PathName.IsForeverRoute(name))
            .Except(listed)];

        Assert.Contains(Group, left);
        Assert.All(left, name => Assert.Contains("\"Requirements\"", File.ReadAllText(Path.Combine(repo.Path, name))));
        Assert.All(listed, name =>
        {
            ApiResult<RecordedPath> read = store.Get(name);
            Assert.Equal(ApiOutcome.Done, read.Outcome);
            Assert.NotEmpty(read.Value!.Points);
        });
    }

    [Theory]
    [InlineData("""[{"X":48.5,"Y":59.1}]""", true)]
    [InlineData("""[ { "z": 1, "x": 2, "y": 3 } ]""", true)]
    [InlineData("// recorded by hand\n[{\"Y\":1,\"X\":2}]", true)]
    [InlineData("[]", true)]
    [InlineData("""[{"Requirements":["Level < 3"]}]""", false)]
    [InlineData("""[{}]""", false)]
    [InlineData("""[[1,2,3]]""", false)]
    [InlineData("""{"X":1}""", false)]
    [InlineData("", false)]
    [InlineData("not json", false)]
    public void A_recorded_path_is_known_by_how_it_starts(string text, bool isPath)
    {
        using TempDataRoot data = new();
        string file = Path.Combine(data.Root, "file.json");
        File.WriteAllText(file, text);

        Assert.Equal(isPath, SharedLib.RecordedPathFile.Problem(file) is null);
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("a/../../escape.json")]
    [InlineData("/etc/escape.json")]
    [InlineData("folder\\escape.json")]
    [InlineData("C:escape.json")]
    [InlineData("notes.txt")]
    [InlineData("a//b.json")]
    [InlineData(" padded.json")]
    [InlineData("")]
    [InlineData("_herb%2FElwynn.json")]
    public void Names_that_could_leave_the_folder_are_refused(string name)
    {
        using TempDataRoot data = new();
        RecordedPathStore store = new(data.Config());

        ApiResult saved = store.Save(name, Points);

        Assert.Equal(ApiOutcome.Invalid, saved.Outcome);
        Assert.False(string.IsNullOrEmpty(saved.Error));
        Assert.Empty(Directory.EnumerateFiles(data.Root, "*", SearchOption.AllDirectories));
    }

    // Windows drops a segment's trailing dot, and a last segment's trailing dots and spaces, so these
    // would open another file there: _forever./x.json is _forever\x.json, a Forever route.
    [Theory]
    [InlineData("_forever./x.json")]
    [InlineData("_FOREVER./x.json")]
    [InlineData("a./b.json")]
    [InlineData("x.json.")]
    [InlineData("x.json ")]
    [InlineData("_forever /x.json")]
    public void Names_that_Windows_would_rewrite_are_refused(string name)
    {
        using TempDataRoot data = new();
        RecordedPathStore store = new(data.Config());

        ApiResult saved = store.Save(name, Points);

        Assert.Equal(ApiOutcome.Invalid, saved.Outcome);
        Assert.Equal(ApiOutcome.Invalid, store.Delete(name).Outcome);
        Assert.Empty(Directory.EnumerateFiles(data.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_dot_inside_a_name_is_plain()
    {
        using TempDataRoot data = new();
        RecordedPathStore store = new(data.Config());

        Assert.Equal(ApiOutcome.Done, store.Save("Troll.Orc/c.json", Points).Outcome);
        Assert.Equal(Points, store.Get("Troll.Orc/c.json").Value!.Points);
    }

    [Fact]
    public void A_name_that_is_another_ones_but_for_case_is_refused_naming_the_stored_one()
    {
        using TempDataRoot data = new();
        RecordedPathStore store = new(data.Config());
        store.Save("_herb/Elwynn.json", Points);

        ApiResult file = store.Save("_herb/elwynn.json", Points);
        ApiResult folder = store.Save("_Herb/Westfall.json", Points);

        // On Windows each is the stored one: a bot that synced both would walk the one it was not given (known issue 71).
        Assert.Equal(ApiOutcome.Conflict, file.Outcome);
        Assert.Contains("save it as '_herb/Elwynn.json'", file.Error);
        Assert.Equal(ApiOutcome.Conflict, folder.Outcome);
        Assert.Contains("save it as '_herb/Westfall.json'", folder.Error);
        Assert.Equal(ApiOutcome.Done, store.Save("_herb/Elwynn.json", Points).Outcome);
        Assert.Equal(ApiOutcome.Done, store.Save("_herb/Westfall.json", Points).Outcome);
    }

    [Fact]
    public void Points_must_be_finite_and_present()
    {
        using TempDataRoot data = new();
        RecordedPathStore store = new(data.Config());

        Assert.Equal(ApiOutcome.Invalid, store.Save("empty.json", []).Outcome);
        Assert.Equal(ApiOutcome.Invalid, store.Save("nan.json", [new(float.NaN, 1, 0)]).Outcome);
    }

    [Fact]
    public void Delete_removes_the_path_and_keeps_its_screenshot()
    {
        using TempDataRoot data = new();
        RecordedPathStore store = new(data.Config());
        store.Save("gone.json", Points);
        string screenshot = Path.Combine(data.Config().Path, "gone.jpg");
        File.WriteAllText(screenshot, "image");

        Assert.Equal(ApiOutcome.Done, store.Delete("gone.json").Outcome);
        Assert.Equal(ApiOutcome.NotFound, store.Get("gone.json").Outcome);
        Assert.Equal(ApiOutcome.NotFound, store.Delete("gone.json").Outcome);
        Assert.True(File.Exists(screenshot));
    }
}
