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
}
