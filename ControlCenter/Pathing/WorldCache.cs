using Contracts;
using Contracts.Pathing;

using Microsoft.Extensions.DependencyInjection;

using ServiceClients;
using ServiceClients.Pathing;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ControlCenter.Pathing;

/// <summary>
/// What the pages pick from - continents, zones, NPC types, places and test routes - read from
/// the service once and again whenever it comes back, not on every render.
/// </summary>
public sealed class WorldCache
{
    private readonly IServiceProvider services;
    private readonly Lock gate = new();
    private Task<WorldSnapshot?>? loading;

    public WorldCache(IServiceProvider services, PathingStatus status)
    {
        this.services = services;
        status.Reconnected += Forget;
    }

    /// <summary>The world, or null while the service cannot be reached.</summary>
    public Task<WorldSnapshot?> GetAsync()
    {
        lock (gate)
        {
            return loading ??= LoadAsync();
        }
    }

    /// <summary>Reads everything again on the next <see cref="GetAsync"/>.</summary>
    public void Forget()
    {
        lock (gate)
        {
            loading = null;
        }
    }

    private async Task<WorldSnapshot?> LoadAsync()
    {
        IPathingClient client = services.GetRequiredService<IPathingClient>();

        ServiceResult<WorldInfo> world = await client.GetWorldAsync();
        if (!world.Ok)
        {
            // Not kept: the next page asks again.
            Forget();
            return null;
        }

        ServiceResult<NamedLocation[]> locations = await client.GetLocationsAsync();
        ServiceResult<TestRoute[]> routes = await client.GetTestRoutesAsync();
        return new WorldSnapshot(world.Value, locations.Value ?? [], routes.Value ?? []);
    }
}

/// <summary>The served client's world as the pages use it.</summary>
public sealed class WorldSnapshot(WorldInfo world, NamedLocation[] locations, TestRoute[] testRoutes)
{
    private readonly FrozenDictionary<int, ZoneInfo> zonesByMap = world.Zones.ToFrozenDictionary(z => z.UiMapId);

    public WorldInfo World { get; } = world;

    public NamedLocation[] Locations { get; } = locations;

    public TestRoute[] TestRoutes { get; } = testRoutes;

    public ZoneInfo? Zone(int uiMapId)
    {
        return zonesByMap.GetValueOrDefault(uiMapId);
    }

    /// <summary>The continent a zone map is on, as the planner names it; null for an unknown map.</summary>
    public string? ContinentOf(int uiMapId)
    {
        return Zone(uiMapId)?.Continent;
    }

    public ContinentInfo? Continent(string name)
    {
        return World.Continents.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether routes can be planned on <paramref name="continent"/>: it has baked tiles.</summary>
    public bool CanRoute(string? continent)
    {
        return continent is not null && Continent(continent) is { Tiles: > 0 };
    }

    /// <summary>The zone a world point is in; any zone of its continent when it lies in none.</summary>
    public ZoneInfo? ZoneAt(int mapId, Point3 world)
    {
        return ZoneMath.ZoneAt(World.Zones, mapId, world.X, world.Y)
               ?? World.Zones.FirstOrDefault(z => z.MapId == mapId);
    }

    public IEnumerable<NamedLocation> LocationsOn(string continent)
    {
        return Locations.Where(l => l.Continent.Equals(continent, StringComparison.OrdinalIgnoreCase));
    }
}
