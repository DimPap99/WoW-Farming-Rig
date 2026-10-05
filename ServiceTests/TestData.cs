using Microsoft.Extensions.Logging.Abstractions;

using PPather.Navmesh;
using PPather.Planning;

using SharedLib;

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

using Xunit;

namespace ServiceTests;

/// <summary>A route of the corpus: a known case from PathingAPI's test routes on a precata continent.</summary>
public sealed record CorpusRoute(string Name, int UiMap, RouteSpace Space, Vector3 From, Vector3 To)
{
    public override string ToString()
    {
        return Name;
    }
}

internal static class TestData
{
    public const string Client = "som";

    public const string NoTiles =
        "needs the baked precata navmesh for Azeroth and Kalimdor (scripts/download-navmesh.ps1 -Era precata)";

    public static string SolutionRoot { get; } = FindSolutionRoot();

    /// <summary>The repository's own data, read-only for these tests.</summary>
    public static string RepoJson => Path.Combine(SolutionRoot, "Json");

    public static DataConfig RepoConfig()
    {
        return new DataConfig { Root = RepoJson, Exp = Client };
    }

    public static bool HasPrecataTiles
    {
        get
        {
            DataConfig config = RepoConfig();
            NavmeshBakeOptions bake = new();
            return Directory.Exists(NavmeshSettings.CacheDirectory(config, bake, "Azeroth")) &&
                   Directory.Exists(NavmeshSettings.CacheDirectory(config, bake, "Kalimdor"));
        }
    }

    public static RoutePlanner NewPlanner()
    {
        DataConfig config = RepoConfig();
        return new RoutePlanner(NullLogger.Instance, config, new WorldMapAreaDB(config),
            new NavmeshBakeOptions(), new NavmeshQueryOptions());
    }

    /// <summary>
    /// Known-trouble routes from <c>PathingAPI/Catalog/TestRoutes.cs</c> on Azeroth and Kalimdor:
    /// open ground, towns, interiors, water and destinations given without a height.
    /// </summary>
    public static IReadOnlyList<CorpusRoute> Corpus { get; } =
    [
        new("Elwynn vendor to path", 1429, RouteSpace.World, new(-8898.32f, -117.35608f, 81.840546f), new(-8779.58f, -106.12012f, 0)),
        new("Elwynn vendor to path, no heights", 1429, RouteSpace.World, new(-8898.32f, -117.35608f, 0), new(-8779.58f, -106.12012f, 0)),
        new("Dun Morogh 1", 1426, RouteSpace.World, new(-6238.128f, 139.40344f, 430.9192f), new(-5884.185f, -118.66675f, 364.64783f)),
        new("Dun Morogh vendor to grind", 1426, RouteSpace.World, new(-6101.417f, 390.9181f, 395.626f), new(-6154.18f, 609.84973f, 395.626f)),
        new("Dun Morogh building", 1426, RouteSpace.Map, new(28.7f, 70.1f, 0), new(28.8f, 67.9f, 0)),
        new("Dun Morogh Coldridge pass", 1426, RouteSpace.Map, new(33.76f, 71.91f, 0), new(39.0f, 61.13f, 0)),
        new("Loch Modan building", 1432, RouteSpace.Map, new(35.2f, 46.9f, 0), new(34.8f, 48.6f, 0)),
        new("Redridge grave to north", 1433, RouteSpace.Map, new(30f, 60f, 0), new(54.3f, 43.1f, 0)),
        new("Durotar Sen'jin to grind", 1411, RouteSpace.World, new(-779.3728f, -4926.2754f, 22.3297f), new(-575.14014f, -4298.2f, 0)),
        new("Durotar Razor Hill to mining", 1411, RouteSpace.World, new(200.97559f, -4785.549f, 13.644421f), new(190.8f, -4795.2f, 14.245161f)),
        new("Orgrimmar to vendor, no start height", 1454, RouteSpace.World, new(1618.5112f, -4433.742f, 0), new(1633.98f, -4439.37f, 15.51f)),
        new("Tanaris flight path to Zul'Farrak", 1446, RouteSpace.Map, new(51.0f, 29.3f, 0), new(38.7f, 20.1f, 0)),
        new("The Barrens search", 1413, RouteSpace.World, new(-896f, -3770f, 11f), new(-441f, -2596f, 96f)),
        new("Teldrassil to vendor", 1438, RouteSpace.World, new(10477.419f, 659.36426f, 1326.2318f), new(10442.9f, 783.989f, 1337.37f)),
    ];

    public static TheoryData<CorpusRoute> CorpusData()
    {
        TheoryData<CorpusRoute> data = [];
        foreach (CorpusRoute route in Corpus)
        {
            data.Add(route);
        }
        return data;
    }

    private static string FindSolutionRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "WoWFarmingRig.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("WoWFarmingRig.sln not found above the test binaries");
    }
}

/// <summary>A data root in a temporary folder, for tests that write; deleted afterwards.</summary>
internal sealed class TempDataRoot : IDisposable
{
    public TempDataRoot(bool withMapData = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "ServiceTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        if (withMapData)
        {
            // The planner needs the client's map areas to start, even when it plans nothing.
            string dbc = Path.Combine(Root, "dbc", TestData.Client);
            Directory.CreateDirectory(dbc);
            File.Copy(Path.Combine(TestData.RepoJson, "dbc", TestData.Client, "WorldMapArea.json"),
                Path.Combine(dbc, "WorldMapArea.json"));
        }
    }

    public string Root { get; }

    public DataConfig Config()
    {
        return new DataConfig { Root = Root, Exp = TestData.Client };
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file watcher may still hold the folder for a moment; the OS cleans temp.
        }
    }
}
