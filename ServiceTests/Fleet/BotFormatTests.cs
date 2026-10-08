using ControlCenter.Components.Bots;
using ControlCenter.Pathing;

using Contracts.Bots;
using Contracts.Pathing;

using System.Collections.Generic;

using Xunit;

namespace ServiceTests.Fleet;

/// <summary>
/// A bot's place in words (known issue 77): its zone was named from the Pathing Service's world whatever
/// the bot played, and another era's maps share their numbers, not their zones.
/// </summary>
public sealed class BotFormatTests
{
    private static readonly WorldSnapshot Precata = new(new WorldInfo("som", "precata", true, [],
        [new ZoneInfo(1411, 14, "Durotar", "Kalimdor", 1, 0, 0, 0, 0)], new Dictionary<string, uint>()), [], []);

    private static readonly BotPosition InDurotar = new(1411, 51.9f, 29.9f);

    [Fact]
    public void A_bot_of_the_services_era_is_placed_in_its_zone()
    {
        Assert.Equal("Durotar 51.9, 29.9", BotFormat.Place(InDurotar, Precata, new AgentGame("hc", "precata", 1, "1.15.7")));
    }

    [Fact]
    public void A_bot_of_another_era_or_none_known_is_placed_on_its_map_number()
    {
        Assert.Equal("map 1411 51.9, 29.9", BotFormat.Place(InDurotar, Precata, new AgentGame("forever", "forever", 1, "1.60.1")));
        Assert.Equal("map 1411 51.9, 29.9", BotFormat.Place(InDurotar, Precata, null));
    }
}
