using Core.Forever;

using Microsoft.Extensions.Logging.Abstractions;

using PPather.Navmesh;

using SharedLib;

using System;
using System.IO;

using Xunit;

namespace AgentTests.Forever;

/// <summary>
/// A Forever run plans with the authored cost zones only when its route applies them: its navmesh is
/// opened with Forever's own zones (<c>Json/road/forever/&lt;continent&gt;</c>), read as the Pathing
/// Service's planner reads them, and with none otherwise, as before.
/// </summary>
public sealed class ForeverCostZonesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "AgentTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_zones_are_forevers_own_and_the_navmesh_plans_with_what_it_is_given()
    {
        DataConfig data = new() { Root = root, Exp = "forever" };
        string durotar = Path.Combine(data.Road, "Kalimdor", "1411.json");
        Directory.CreateDirectory(Path.GetDirectoryName(durotar)!);
        File.Copy(Path.Combine(RepoJson, "road", "precata", "Kalimdor", "1411.json"), durotar);

        CostZones zones = ForeverNavmesh.CostZonesOf(NullLogger.Instance, data, "Kalimdor");
        using NavmeshPathfinder applied = ForeverNavmesh.OpenDiskOnly(NullLogger.Instance, Path.Combine(root, "tiles"), zones);
        using NavmeshPathfinder ignored = ForeverNavmesh.OpenDiskOnly(NullLogger.Instance, Path.Combine(root, "tiles"));

        Assert.False(zones.IsEmpty);
        Assert.True(ForeverNavmesh.CostZonesOf(NullLogger.Instance, data, "Azeroth").IsEmpty);
        Assert.Same(zones, applied.Zones);
        Assert.Same(CostZones.Empty, ignored.Zones);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string RepoJson
    {
        get
        {
            string? dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "WoWFarmingRig.sln")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            return Path.Combine(dir ?? throw new DirectoryNotFoundException("WoWFarmingRig.sln"), "Json");
        }
    }
}
