using BotAgent.Sync;

using System;
using System.Collections.Generic;
using System.IO;

using Xunit;

namespace ServiceTests.Agent;

/// <summary>The paths a class profile names, as the sync reads them without the bot.</summary>
public sealed class ProfilePathsTests
{
    /// <summary>A profile written by hand, naming a path in every place one can be named.</summary>
    public const string Profile = """
        {
          // The old single path, still read when "Paths" is missing.
          "PathFilename": "legacy.json",
          "Paths": [
            { "PathFilename": "_pack\\1-20\\Human\\a b.json", },
            { "pathfilename": "\\_p2\\lower.json", "OverridePathFilename": "" },
            { "Generate": { "Subzone": "Northshire Valley", "Mode": "Wander" } },
          ],
          "PathsFilenames": [ "group.json" ],
          "NPC": { "Sequence": [ { "Name": "Repair", "PathFilename": "vendor.json" } ] },
          "Adhoc": { "Sequence": [ { "Name": "Elsewhere", "PathFilename": "C:\\elsewhere\\abs.json" } ] },
        }
        """;

    /// <summary>A path group file: a bare list of what a profile's <c>Paths</c> holds.</summary>
    public const string Group = """
        // Northshire, levels 1-3.
        [
          { "Requirements": [ "Level < 3" ], "PathFilename": "grouped.json" },
          { "Generate": { "Subzone": "Northshire Valley" } }
        ]
        """;

    [Fact]
    public void A_profile_names_its_paths_wherever_it_puts_them()
    {
        Assert.True(ProfilePaths.TryRead(Profile, out ProfileFiles files, out string error), error);

        Assert.Equal(["legacy.json", "_pack/1-20/Human/a b.json", "_p2/lower.json", "vendor.json"], files.Paths);
        Assert.Equal(["group.json"], files.Groups);
        Assert.Equal(["C:\\elsewhere\\abs.json"], files.Skipped);
    }

    [Fact]
    public void A_path_group_names_paths_as_a_profile_does()
    {
        Assert.True(ProfilePaths.TryRead(Group, out ProfileFiles files, out string error), error);

        Assert.Equal(["grouped.json"], files.Paths);
        Assert.Empty(files.Groups);
    }

    [Theory]
    [InlineData("_pack\\1-20\\Human\\x.json", "_pack/1-20/Human/x.json")]
    [InlineData("\\_pack\\x.json", "_pack/x.json")]
    [InlineData("/_pack/x.json", "_pack/x.json")]
    [InlineData("plain.json", "plain.json")]
    public void A_name_is_read_as_the_bot_opens_it(string given, string library)
    {
        Assert.Equal(library, ProfilePaths.Normalize(given));
    }

    [Fact]
    public void A_file_that_is_not_json_says_why()
    {
        Assert.False(ProfilePaths.TryRead("{ \"PathFilename\": ", out ProfileFiles files, out string error));
        Assert.NotEmpty(error);
        Assert.Empty(files.Paths);
    }

    [Fact]
    public void Every_profile_in_the_repository_names_only_paths_the_library_has()
    {
        string paths = Path.Combine(TestData.RepoJson, "path");
        string[] profiles = Directory.GetFiles(Path.Combine(TestData.RepoJson, "class"), "*.json", SearchOption.AllDirectories);
        HashSet<string> named = new(StringComparer.Ordinal);
        foreach (string profile in profiles)
        {
            Assert.True(ProfilePaths.TryReadFile(profile, out ProfileFiles files, out string error), $"{profile}: {error}");
            Assert.Empty(files.Skipped);
            named.UnionWith(files.Paths);
            foreach (string group in files.Groups)
            {
                string file = Path.Combine(paths, group);
                Assert.True(ProfilePaths.TryReadFile(file, out ProfileFiles grouped, out error), $"{profile} names the group {group}: {error}");
                named.UnionWith(grouped.Paths);
            }
        }

        // Backslashes, a leading one and spaces included, every name is a file of the library, letter case too.
        Assert.True(profiles.Length > 90, $"{profiles.Length} profiles");
        Assert.True(named.Count > 50, $"{named.Count} paths");
        foreach (string name in named)
        {
            Assert.True(File.Exists(Path.Combine(paths, name)), $"{name} is not in Json/path");
        }
    }
}
