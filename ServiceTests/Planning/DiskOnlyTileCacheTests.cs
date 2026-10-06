using Microsoft.Extensions.Logging.Abstractions;

using PPather.Navmesh;

using SharedLib;

using System;
using System.IO;

using Xunit;

namespace ServiceTests.Planning;

/// <summary>
/// A disk-only tile cache (a planner lane, a bot without game files) never bakes, so a tile it
/// cannot read is one it must keep (known issue 39): a writer, a bake in another process or
/// download-navmesh.ps1, may not have finished it. It reads it again on a later request.
/// </summary>
public sealed class DiskOnlyTileCacheTests
{
    // Goldshire's vendor, on walkable ground.
    private const float X = -8898.32f;
    private const float Y = -117.35608f;

    [Fact]
    public void A_tile_it_cannot_read_is_kept_and_read_again_once_whole()
    {
        Assert.SkipUnless(TestData.HasPrecataTiles, TestData.NoTiles);
        NavmeshCoords.GetTileIndex(X, Y, out int tileX, out int tileZ);
        string source = NavmeshTileFiles.PathOf(
            NavmeshSettings.CacheDirectory(TestData.RepoConfig(), new NavmeshBakeOptions(), "Azeroth"), tileX, tileZ);
        using TempDataRoot data = new();
        string tile = NavmeshTileFiles.PathOf(data.Root, tileX, tileZ);
        byte[] whole = File.ReadAllBytes(source);
        // As a writer leaves it halfway.
        File.WriteAllBytes(tile, whole.AsSpan(0, whole.Length / 2).ToArray());
        using NavmeshPathfinder nav = new(NullLogger.Instance, null, data.Root, new NavmeshBakeOptions(), new NavmeshQueryOptions());

        bool halfRead = nav.TryGetHeight(X, Y, out _);

        Assert.False(halfRead);
        Assert.Equal(whole.Length / 2, new FileInfo(tile).Length);

        File.WriteAllBytes(tile, whole);
        bool wholeRead = nav.TryGetHeight(X, Y, out float z);

        Assert.True(wholeRead);
        Assert.InRange(z, 70f, 95f);
        Assert.Contains((tileX, tileZ), nav.Tiles.ResidentTiles);
    }
}
