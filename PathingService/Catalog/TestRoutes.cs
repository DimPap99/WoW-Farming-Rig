using Contracts;
using Contracts.Pathing;

using SharedLib;

using System.Collections.Generic;
using System.Linq;

namespace PathingService.Catalog;

/// <summary>
/// Known-trouble routes collected while tuning the engine ("Z ..." = start or end without a
/// height), as the requests to send. Each runs exactly what any caller runs, and the answer shows
/// in every viewer. Only the routes on zone maps the served client has are offered.
/// </summary>
public sealed class TestRoutes(WorldMapAreaDB maps)
{
    private static readonly IReadOnlyList<TestRoute> AllRoutes = Build();

    public TestRoute[] All => [.. AllRoutes.Where(r => maps.TryGet(r.Forward.UiMapId, out _))];

    private static TestRoute[] Build()
    {
        List<TestRoute> r = [];

        // World coordinates; the zone only picks the continent.
        void World(string zone, string name, int uiMap, float x1, float y1, float z1, float x2, float y2, float z2,
            bool withReverse = false)
        {
            Point3 from = new(x1, y1, z1);
            Point3 to = new(x2, y2, z2);
            r.Add(new(zone, name, new RouteRequest(null, uiMap, CoordinateSpace.World, from, to),
                withReverse ? new RouteRequest(null, uiMap, CoordinateSpace.World, to, from) : null));
        }

        // The zone's map coordinates, 0 to 100.
        void Map(string zone, string name, int uiMap, float x1, float y1, float x2, float y2)
        {
            Point3 from = new(x1, y1, 0);
            Point3 to = new(x2, y2, 0);
            r.Add(new(zone, name, new RouteRequest(null, uiMap, CoordinateSpace.Map, from, to),
                new RouteRequest(null, uiMap, CoordinateSpace.Map, to, from)));
        }

        World("Alterac Mountains", "Horde grave", 1416, -17.51f, -986.82f, 55.83f, 305.33337f, -364.6667f, 168.28902f, withReverse: true);

        Map("Ammen Vale", "Ammen Vale", 1943, 81.05f, 45.33f, 78.92f, 44.14f);
        World("Ammen Vale", "Z Vendor", 1943, -4237.333f, -13526.44f, 0, -4226.534f, -12520.762f, 0);

        World("Azshara", "Z issue no result", 1447, 2293.7908f, -6636.0283f, 120.13607f, 2309.54f, -6666.62f, 0);
        Map("Azshara", "Azshara 1", 1447, 67.25f, 82.88f, 66.68f, 90.23f);

        World("Coldridge Valley", "Z Coldridge to 5_gnome", 1426, -6120.8084f, 542.90857f, 0, -5880.7373f, -116.15503f, 0, withReverse: true);

        World("Dun Morogh", "Dun Morogh 1", 1426, -6238.128f, 139.40344f, 430.9192f, -5884.185f, -118.66675f, 364.64783f, withReverse: true);
        World("Dun Morogh", "Vendor to grind", 1426, -6101.417f, 390.9181f, 395.626f, -6154.18f, 609.84973f, 395.626f);
        World("Dun Morogh", "Dun Morogh 2", 1426, -5609.00f, -479.00f, 397.49f, -5884.185f, -118.66675f, 364.64783f, withReverse: true);
        World("Dun Morogh", "Z Vendor Rybrad Coldbank", 1426, -6200.1924f, 700.5774f, 384.6462f, -6103.1836f, 393.5332f, 0, withReverse: true);
        World("Dun Morogh", "Vendor Rybrad Coldbank", 1426, -6200.1924f, 700.5774f, 384.6462f, -6103.1836f, 393.5332f, 396.0979f, withReverse: true);
        Map("Dun Morogh", "Building Grundel Harkin", 1426, 28.7f, 70.1f, 28.8f, 67.9f);
        Map("Dun Morogh", "Coldridge pass - unable to find", 1426, 33.90f, 71.86f, 38.94f, 61.0f);
        Map("Dun Morogh", "Coldridge pass", 1426, 33.76f, 71.91f, 39.0f, 61.13f);

        World("Durotar", "Z Sen'jin village to grind", 1411, -779.3728f, -4926.2754f, 0, -575.14014f, -4298.2f, 0, withReverse: true);
        World("Durotar", "Sen'jin village to grind", 1411, -779.3728f, -4926.2754f, 22.3297f, -575.14014f, -4298.2f, 0, withReverse: true);
        World("Durotar", "Razor Hill to mining", 1411, 200.97559f, -4785.549f, 13.644421f, 190.8f, -4795.2f, 14.245161f, withReverse: true);

        Map("Duskwood", "Duskwood issue", 1431, 43.097f, 18.38f, 45.34f, 16.438f);

        World("Elwynn Forest", "Vendor to path", 1429, -8898.32f, -117.35608f, 81.840546f, -8779.58f, -106.12012f, 0, withReverse: true);
        World("Elwynn Forest", "Z Vendor to path", 1429, -8898.32f, -117.35608f, 0, -8779.58f, -106.12012f, 0);

        World("Hellfire Peninsula", "Z Honor Hold issue no result", 1944, -732.031f, 2448.2805f, 58.940506f, -755.79004f, 2491.16f, 0);
        Map("Hellfire Peninsula", "Hellfire 1", 1944, 60.0762f, 43.4372f, 61.1473f, 39.955284f);
        Map("Hellfire Peninsula", "Hellfire 2", 1944, 60.0762f, 43.4372f, 61.14f, 38.38f);

        Map("Hinterlands", "Water issue 1", 1425, 82.2771f, 48.698803f, 81.65922f, 49.87935f);
        Map("Hinterlands", "Water issue 2", 1425, 80.7258f, 64.2714f, 78.321304f, 64.0448f);

        Map("Loch Modan", "Building Yanni Stoutheart", 1432, 35.2f, 46.9f, 34.8f, 48.6f);
        Map("Loch Modan", "Building innkeeper", 1432, 35.2f, 46.9f, 35.5f, 48.5f);
        Map("Loch Modan", "Building Vidra Heartstove", 1432, 35.2f, 46.9f, 34.8f, 49.1f);

        World("Orgrimmar", "Z Orgrimmar to vendor", 1454, 1618.5112f, -4433.742f, 0, 1633.98f, -4439.37f, 15.51f);

        Map("Redridge Mountains", "Grave to North", 1433, 30, 60, 54.3f, 43.1f);
        Map("Silithus", "Inn to AQ", 1451, 51.4f, 37.8f, 29, 92);
        Map("Tanaris", "FP to ZF", 1446, 51.0f, 29.3f, 38.7f, 20.1f);

        World("Teldrassil", "Teldrassil to vendor", 1438, 10477.419f, 659.36426f, 1326.2318f, 10442.9f, 783.989f, 1337.37f, withReverse: true);
        World("The Barrens", "Search Barrens", 1413, -896, -3770, 11, -441, -2596, 96, withReverse: true);

        Map("Zul'Drak", "Stairs", 121, 40.4058f, 63.024403f, 40.3816f, 64.259705f);

        return [.. r];
    }
}
