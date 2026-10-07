using Contracts;
using Contracts.Pathing;

using ServiceClients.Security;

using System.Net;

using Xunit;

namespace ServiceTests.Clients;

/// <summary>
/// The rules a forwarder applies to its own caller (known issue 47), and the cross-site rule every
/// service and forwarder applies to a browser's writes.
/// </summary>
public sealed class ForwardPolicyTests
{
    private const string Key = "the-key";

    private static readonly IPAddress OtherMachine = IPAddress.Parse("192.0.2.10");

    [Theory]
    [InlineData("PUT", PathingApi.Paths + "/16_Loch Modan.json", true)]
    [InlineData("PUT", PathingApi.Paths + "/_herb/x.json", true)]
    [InlineData("PUT", PathingApi.CostZones + "/Azeroth/" + PathingApi.Roads + "/1429", true)]
    [InlineData("PUT", PathingApi.CostZones + "/Azeroth/" + PathingApi.Danger + "/1429", true)]
    [InlineData("DELETE", PathingApi.Paths + "/16_Loch Modan.json", false)]
    [InlineData("POST", PathingApi.Bake, false)]
    [InlineData("POST", PathingApi.BakeCancel, false)]
    [InlineData("POST", PathingApi.AreaGrids, false)]
    [InlineData("DELETE", PathingApi.Navmesh + "/Azeroth", false)]
    [InlineData("POST", PathingApi.CostZones + "/reload", false)]
    [InlineData("PUT", PathingApi.CostZones + "/Azeroth/other/1429", false)]
    [InlineData("PUT", PathingApi.ForeverRoutes + "/route.txt", false)]
    public void The_bots_map_authors_paths_and_zones_and_nothing_else(string method, string path, bool authors)
    {
        Assert.Equal(authors, ForwardPolicy.LeafletMapAuthors(method, path));
    }

    [Fact]
    public void This_machine_and_the_key_get_anything_another_machine_reads_and_its_pages_authoring()
    {
        string? local = ForwardPolicy.Refusal("DELETE", PathingApi.Navmesh + "/Azeroth", IPAddress.Loopback, null, null,
            ForwardPolicy.LeafletMapAuthors, forwarded: false);
        string? read = ForwardPolicy.Refusal("GET", PathingApi.World, OtherMachine, null, null, ForwardPolicy.LeafletMapAuthors, forwarded: false);
        string? saved = ForwardPolicy.Refusal("PUT", PathingApi.CostZones + "/Azeroth/roads/1429", OtherMachine, null, null,
            ForwardPolicy.LeafletMapAuthors, forwarded: false);
        string? cleared = ForwardPolicy.Refusal("DELETE", PathingApi.Navmesh + "/Azeroth", OtherMachine, Key, null,
            ForwardPolicy.LeafletMapAuthors, forwarded: false);
        string? clearedWithKey = ForwardPolicy.Refusal("DELETE", PathingApi.Navmesh + "/Azeroth", OtherMachine, Key, Key,
            ForwardPolicy.LeafletMapAuthors, forwarded: false);
        string? looped = ForwardPolicy.Refusal("GET", PathingApi.World, IPAddress.Loopback, null, null,
            ForwardPolicy.LeafletMapAuthors, forwarded: true);

        Assert.Null(local);
        Assert.Null(read);
        Assert.Null(saved);
        Assert.NotNull(cleared);
        Assert.Contains(ServiceKey.Header, cleared);
        Assert.Null(clearedWithKey);
        Assert.Contains("forwarded", looped);
    }

    [Theory]
    [InlineData("GET", "cross-site", null, true)]
    [InlineData("POST", "same-origin", null, true)]
    [InlineData("POST", "none", null, true)]
    [InlineData("POST", "cross-site", null, false)]
    [InlineData("POST", "same-site", null, false)]
    [InlineData("DELETE", "cross-site", null, false)]
    [InlineData("POST", null, "http://localhost:5010", true)]
    [InlineData("POST", null, "http://evil.example", false)]
    [InlineData("POST", null, "null", false)]
    [InlineData("POST", null, null, true)]
    [InlineData("OPTIONS", "cross-site", null, true)]
    public void A_write_another_site_made_a_browser_send_is_refused(string method, string? fetchSite, string? origin, bool served)
    {
        string? refusal = ServiceKeyPolicy.CrossSiteRefusal(method, fetchSite, origin, "localhost:5010");

        Assert.Equal(served, refusal is null);
    }
}
