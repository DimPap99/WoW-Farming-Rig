using Contracts.Bots;

using ServiceClients;
using ServiceClients.Bots;

using System.IO;
using System.Linq;

using Xunit;

namespace ServiceTests.Clients;

/// <summary>
/// The one rule for a library file's name (<see cref="LibraryName"/>), which the Pathing Service, the
/// manager, the Control Center and the agent all apply. Two copies of it drifted: the service's alone
/// refused <c>%</c>, a segment ending in a dot and the Forever route folder (known issue 68), and both took
/// <c>.JSON</c>, which the libraries never list on Linux (known issue 70).
/// </summary>
public sealed class LibraryNameTests
{
    [Theory]
    [InlineData("trip.json", true)]
    [InlineData("_pack/1-20/Night elf.json", true)]
    [InlineData("a.b/c.json", true)]
    [InlineData("Durotar.JSON", false)]
    [InlineData("a%2Fb.json", false)]
    [InlineData("_forever./x.json", false)]
    [InlineData("x.json.", false)]
    [InlineData("../x.json", false)]
    [InlineData("/x.json", false)]
    [InlineData("a//b.json", false)]
    [InlineData(" a.json", false)]
    [InlineData(".json", false)]
    [InlineData("x.txt", false)]
    public void A_library_name_is_relative_plain_and_ends_in_json_exactly(string name, bool valid)
    {
        Assert.Equal(valid, LibraryName.IsRelativeName(name));
    }

    [Fact]
    public void A_recorded_path_is_never_in_the_Forever_route_folder()
    {
        Assert.False(LibraryName.IsRecordedPathName("_forever/trip.json"));
        Assert.False(LibraryName.IsRecordedPathName("_FOREVER/trip.json"));
        Assert.True(LibraryName.IsRecordedPathName("forever/trip.json"));
    }

    [Theory]
    [InlineData("_forever/trip.json")]
    [InlineData("a%2Fb.json")]
    [InlineData("a./b.json")]
    [InlineData("x.JSON")]
    public void An_assignment_refuses_a_path_the_service_refuses(string path)
    {
        Assert.NotNull(AssignmentRules.Problem(BotAssignment.ForClassic("Hunter.json", path)));
    }

    [Fact]
    public void An_assignment_refuses_a_route_named_JSON()
    {
        Assert.NotNull(AssignmentRules.Problem(BotAssignment.ForForever("Durotar.JSON", 30)));
    }

    // The route library, the web page's route list, the path store, the sync and the agent's profile list
    // each listed a folder their own way: other files, another order, names a request could not open.
    [Fact]
    public void A_library_lists_what_a_request_could_name_by_name_ignoring_case()
    {
        DirectoryInfo folder = Directory.CreateTempSubdirectory("library-");
        try
        {
            foreach (string name in new[] { "b.json", "A.json", "bad%.json", "notes.txt", "sub/d.json", "sub/x.json./e.json" })
            {
                string file = Path.Join(folder.FullName, name);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "[]");
            }

            Assert.Equal(["A.json", "b.json"], LibraryName.Files(folder.FullName, nested: false).Select(entry => entry.Name));
            Assert.Equal(["A.json", "b.json", "sub/d.json"], LibraryName.Files(folder.FullName, nested: true).Select(entry => entry.Name));
            Assert.Equal(Path.Join(folder.FullName, "sub", "d.json"), LibraryName.Files(folder.FullName, nested: true)[2].File);
            Assert.Empty(LibraryName.Files(Path.Join(folder.FullName, "missing"), nested: true));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
