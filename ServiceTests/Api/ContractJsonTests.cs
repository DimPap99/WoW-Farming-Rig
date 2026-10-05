using Contracts;
using Contracts.Pathing;

using System.Text.Json;

using Xunit;

namespace ServiceTests.Api;

public sealed class ContractJsonTests
{
    [Fact]
    public void Requests_travel_camel_cased_with_named_enums_and_come_back_equal()
    {
        RouteRequest request = new("som", 1429, CoordinateSpace.Map, new(51f, 29.3f, 0), new(38.7f, 20.1f, 0),
            new RouteTuning(2f, 7));

        string json = JsonSerializer.Serialize(request, ContractJson.Options);

        Assert.Contains("\"uiMapId\":1429", json);
        Assert.Contains("\"space\":\"Map\"", json);
        Assert.Equal(request, JsonSerializer.Deserialize<RouteRequest>(json, ContractJson.Options));
    }

    [Fact]
    public void Cost_zone_sets_keep_their_uimap_keys()
    {
        CostZoneSet set = new("Azeroth",
            new() { [1429] = new RoadZones([new RoadLine("road", 2, [new(1, 2)])]) },
            new() { [1426] = new DangerZones([new DangerCircle("camp", 1, 2, 3, 4, ZoneMode.Block)], []) });

        CostZoneSet back = JsonSerializer.Deserialize<CostZoneSet>(JsonSerializer.Serialize(set, ContractJson.Options), ContractJson.Options)!;

        Assert.Equal(set.Roads[1429].Roads[0].Points, back.Roads[1429].Roads[0].Points);
        Assert.Equal(ZoneMode.Block, back.Danger[1426].Circles[0].Mode);
    }

    [Fact]
    public void World_info_keeps_its_zone_bounds_and_npc_flags()
    {
        WorldInfo world = new("som", "precata", true, [new ContinentInfo("Azeroth", 0, 10590)],
            [new ZoneInfo(1429, 12, "Elwynn Forest", "Azeroth", 0, 1535.4166f, -1935.4166f, -7939.583f, -10254.166f)],
            new() { ["vendor"] = 128 });

        WorldInfo back = RoundTrip(world);

        Assert.Equal(world.Zones[0], back.Zones[0]);
        Assert.Equal(world.Continents[0], back.Continents[0]);
        Assert.Equal(128u, back.NpcFlags["vendor"]);
    }

    [Fact]
    public void Test_routes_carry_their_requests()
    {
        TestRoute route = new("Elwynn Forest", "Vendor to path",
            new RouteRequest(null, 1429, CoordinateSpace.World, new(-8898.32f, -117.35f, 81.84f), new(-8779.58f, -106.12f, 0)),
            null);

        TestRoute back = RoundTrip(route);

        Assert.Equal(route.Forward, back.Forward);
        Assert.Null(back.Reverse);
    }

    [Fact]
    public void Bake_and_watch_messages_round_trip_with_named_enums()
    {
        BakeStatus status = new(true, "continent", "Azeroth", 40, 3, 48, 30, 18, 12.5, "Azeroth adt 30,49");
        WatchPath path = new("route", 1429, CoordinateSpace.Map, [new(48.5f, 59.1f, 0)], WatchColor.Teal);
        PathingCapabilities capabilities = new(PathingApi.Version, "som", "precata", true, ["Azeroth"], GameFiles: false);

        string json = JsonSerializer.Serialize(path, ContractJson.Options);

        Assert.Contains("\"color\":\"Teal\"", json);
        Assert.Equal(status, RoundTrip(status));
        Assert.Equal(path.Points, RoundTrip(path).Points);
        Assert.Equal(new BakeRequest("Azeroth", 30, 49), RoundTrip(new BakeRequest("Azeroth", 30, 49)));
        Assert.False(RoundTrip(capabilities).GameFiles);
    }

    private static T RoundTrip<T>(T value)
    {
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ContractJson.Options), ContractJson.Options)!;
    }
}
