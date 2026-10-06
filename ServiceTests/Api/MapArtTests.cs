using Contracts;
using Contracts.Pathing;

using PathingService.World;

using ServiceClients;
using ServiceClients.Pathing;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Xunit;

namespace ServiceTests.Api;

/// <summary>
/// Where the map art lies: the service's table against BlazorServer's Leaflet map, which keeps its
/// own copy of the numbers (<c>leaflet-watch.js</c>), and what the world description says of it.
/// </summary>
public sealed partial class MapArtTests
{
    [Fact]
    public void The_art_table_matches_the_leaflet_map()
    {
        Dictionary<(string Era, string Continent), MapArt> leaflet = LeafletConfigs();

        Assert.NotEmpty(leaflet);
        Assert.Equal(leaflet.Keys.Order(), MapArtTable.All.Keys.Order());
        foreach (((string era, string continent), MapArt art) in leaflet)
        {
            Assert.Equal(art, MapArtTable.All[(era, continent)]);
        }
    }

    [Fact]
    public async Task Continents_carry_their_art_where_its_tiles_are_downloaded()
    {
        Assert.SkipUnless(Directory.Exists(Path.Combine(TestData.RepoJson, "leaflet", "precata", "Kalimdor")), "needs the precata minimap tiles");
        using PathingServiceFactory factory = new(TestData.RepoJson);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<WorldInfo> world = await client.GetWorldAsync(TestContext.Current.CancellationToken);

        Assert.True(world.Ok, world.Error);
        ContinentInfo kalimdor = Assert.Single(world.Value.Continents, c => c.Name == "Kalimdor");
        Assert.Equal(MapArtTable.All[("precata", "Kalimdor")], kalimdor.Art);
    }

    [Fact]
    public async Task A_continent_without_its_tiles_has_no_art()
    {
        using TempDataRoot data = new(withMapData: true);
        using PathingServiceFactory factory = new(data.Root);
        PathingClient client = new(factory.CreateClient());

        ServiceResult<WorldInfo> world = await client.GetWorldAsync(TestContext.Current.CancellationToken);

        Assert.True(world.Ok, world.Error);
        Assert.All(world.Value.Continents, c => Assert.Null(c.Art));
    }

    // leaflet-watch.js's Configs: { era: { continent: { resX, resY, maxZoom, offset: { min: { x, y } } } } }.
    // JS, not JSON - unquoted keys, comments - so it is scanned: comments out, then brace by brace.
    private static Dictionary<(string, string), MapArt> LeafletConfigs()
    {
        string script = File.ReadAllText(Path.Combine(TestData.SolutionRoot, "MapAssets", "wwwroot", "script", "leaflet-watch.js"));
        string text = CommentRegex().Replace(script, string.Empty);
        int start = text.IndexOf("const Configs", StringComparison.Ordinal);
        string configs = Block(text, text.IndexOf('{', start));

        Dictionary<(string, string), MapArt> result = [];
        foreach ((string era, string eraBlock) in Entries(configs))
        {
            foreach ((string continent, string block) in Entries(eraBlock))
            {
                Match min = OffsetMinRegex().Match(block);
                result[(era, continent)] = new MapArt(Number(min.Groups[2]), Number(min.Groups[1]),
                    Number(ResXRegex().Match(block).Groups[1]), Number(ResYRegex().Match(block).Groups[1]),
                    MapArtTable.MinZoom, Number(MaxZoomRegex().Match(block).Groups[1]));
            }
        }

        return result;
    }

    // The 'key': { ... } entries directly inside a block.
    private static IEnumerable<(string Key, string Block)> Entries(string block)
    {
        int depth = 0;
        for (int i = 0; i < block.Length; i++)
        {
            if (block[i] == '{')
            {
                depth++;
            }
            else if (block[i] == '}')
            {
                depth--;
            }
            else if (block[i] == '\'' && depth == 1)
            {
                int end = block.IndexOf('\'', i + 1);
                string inner = Block(block, block.IndexOf('{', end));
                yield return (block[(i + 1)..end], inner);
                i = block.IndexOf('{', end) + inner.Length - 1;
            }
        }
    }

    private static string Block(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            depth += text[i] == '{' ? 1 : text[i] == '}' ? -1 : 0;
            if (depth == 0)
            {
                return text[open..(i + 1)];
            }
        }

        throw new InvalidDataException("unbalanced braces in leaflet-watch.js");
    }

    [GeneratedRegex("//[^\n]*")]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"min\s*:\s*\{\s*x\s*:\s*(\d+)\s*,\s*y\s*:\s*(\d+)")]
    private static partial Regex OffsetMinRegex();

    [GeneratedRegex(@"resX\s*:\s*(\d+)")]
    private static partial Regex ResXRegex();

    [GeneratedRegex(@"resY\s*:\s*(\d+)")]
    private static partial Regex ResYRegex();

    [GeneratedRegex(@"maxZoom\s*:\s*(\d+)")]
    private static partial Regex MaxZoomRegex();

    private static int Number(Group group)
    {
        Assert.True(group.Success, "a number leaflet-watch.js should have is missing");
        return int.Parse(group.Value, CultureInfo.InvariantCulture);
    }
}
