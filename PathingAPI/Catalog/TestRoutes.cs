using System;
using System.Collections.Generic;

using static System.FormattableString;

namespace PathingAPI.Catalog;

/// <summary>
/// One request against the HTTP API, with the map it is on so the UI can tell
/// whether this install has tiles for it. <see cref="UiMap"/> is set when the route
/// is in zone coordinates and <see cref="MapId"/> when it is in world coordinates.
/// </summary>
public sealed record TestRoute(string Zone, string Name, string Url, string? ReverseUrl, int? UiMap, int? MapId);

/// <summary>
/// Known-trouble routes collected while tuning the engine ("Z ..." = start or end
/// without a height). Each one is a plain API call, so running it exercises
/// exactly what the bot does - the gate, the 429, the response - and the result
/// shows in every viewer.
/// </summary>
public static class TestRoutes
{
    public static readonly IReadOnlyList<TestRoute> All = Build();

    private static TestRoute[] Build()
    {
        List<TestRoute> r = [];

        void World(string zone, string name, double x1, double y1, double z1, double x2, double y2, double z2,
            int mapId, bool withReverse = false)
        {
            string url = Invariant($"api/PPather/WorldRoute?x1={x1}&y1={y1}&z1={z1}&x2={x2}&y2={y2}&z2={z2}&mapid={mapId}");
            r.Add(new(zone, name, url + "&reverse=false", withReverse ? url + "&reverse=true" : null, null, mapId));
        }

        // WorldRoute2 takes its continent from uimap alone.
        void World2(string zone, string name, double x1, double y1, double z1, double x2, double y2, double z2,
            int uiMap, bool startIndoors = false)
        {
            string url = Invariant($"api/PPather/WorldRoute2?x1={x1}&y1={y1}&z1={z1}&x2={x2}&y2={y2}&z2={z2}&uimap={uiMap}")
                + (startIndoors ? "&startindoors=true" : "&startindoors=false");
            r.Add(new(zone, name, url, null, uiMap, null));
        }

        void Map(string zone, string name, int uiMap, double x1, double y1, double x2, double y2)
        {
            string url = Invariant($"api/PPather/MapRoute?uimap1={uiMap}&x1={x1}&y1={y1}&uimap2={uiMap}&x2={x2}&y2={y2}");
            string back = Invariant($"api/PPather/MapRoute?uimap1={uiMap}&x1={x2}&y1={y2}&uimap2={uiMap}&x2={x1}&y2={y1}");
            r.Add(new(zone, name, url, back, uiMap, null));
        }

        World("Alterac Mountains", "Horde grave", -17.51, -986.82, 55.83, 305.33337, -364.6667, 168.28902, 0, withReverse: true);

        Map("Ammen Vale", "Ammen Vale", 1943, 81.05, 45.33, 78.92, 44.14);
        World2("Ammen Vale", "Z Vendor", -4237.333, -13526.44, 0, -4226.534, -12520.762, 0, 1943);

        World2("Azshara", "Z issue no result", 2293.7908, -6636.0283, 120.13607, 2309.54, -6666.62, 0, uiMap: 1447);
        Map("Azshara", "Azshara 1", 1447, 67.25, 82.88, 66.68, 90.23);

        World("Coldridge Valley", "Z Coldridge to 5_gnome", -6120.8084, 542.90857, 0, -5880.7373, -116.15503, 0, 0, withReverse: true);

        World("Dun Morogh", "Dun Morogh 1", -6238.128, 139.40344, 430.9192, -5884.185, -118.66675, 364.64783, 0, withReverse: true);
        World2("Dun Morogh", "Vendor to grind", -6101.417, 390.9181, 395.626, -6154.18, 609.84973, 395.626, uiMap: 1426, startIndoors: true);
        World("Dun Morogh", "Dun Morogh 2", -5609.00, -479.00, 397.49, -5884.185, -118.66675, 364.64783, 0, withReverse: true);
        World("Dun Morogh", "Z Vendor Rybrad Coldbank", -6200.1924, 700.5774, 384.6462, -6103.1836, 393.5332, 0, 0, withReverse: true);
        World("Dun Morogh", "Vendor Rybrad Coldbank", -6200.1924, 700.5774, 384.6462, -6103.1836, 393.5332, 396.0979, 0, withReverse: true);
        Map("Dun Morogh", "Building Grundel Harkin", 1426, 28.7, 70.1, 28.8, 67.9);
        Map("Dun Morogh", "Coldridge pass - unable to find", 1426, 33.90, 71.86, 38.94, 61.0);
        Map("Dun Morogh", "Coldridge pass", 1426, 33.76, 71.91, 39.0, 61.13);

        World("Durotar", "Z Sen'jin village to grind", -779.3728, -4926.2754, 0, -575.14014, -4298.2, 0, 1, withReverse: true);
        World("Durotar", "Sen'jin village to grind", -779.3728, -4926.2754, 22.3297, -575.14014, -4298.2, 0, 1, withReverse: true);
        World("Durotar", "Razor Hill to mining", 200.97559, -4785.549, 13.644421, 190.8, -4795.2, 14.245161, 1, withReverse: true);

        Map("Duskwood", "Duskwood issue", 1431, 43.097, 18.38, 45.34, 16.438);

        World("Elwynn Forest", "Vendor to path", -8898.32, -117.35608, 81.840546, -8779.58, -106.12012, 0, 0, withReverse: true);
        World2("Elwynn Forest", "Z Vendor to path", -8898.32, -117.35608, 0, -8779.58, -106.12012, 0, uiMap: 1429);

        World2("Hellfire Peninsula", "Z Honor Hold issue no result", -732.031, 2448.2805, 58.940506, -755.79004, 2491.16, 0, uiMap: 1944);
        Map("Hellfire Peninsula", "Hellfire 1", 1944, 60.0762, 43.4372, 61.1473, 39.955284);
        Map("Hellfire Peninsula", "Hellfire 2", 1944, 60.0762, 43.4372, 61.14, 38.38);

        Map("Hinterlands", "Water issue 1", 1425, 82.2771, 48.698803, 81.65922, 49.87935);
        Map("Hinterlands", "Water issue 2", 1425, 80.7258, 64.2714, 78.321304, 64.0448);

        Map("Loch Modan", "Building Yanni Stoutheart", 1432, 35.2, 46.9, 34.8, 48.6);
        Map("Loch Modan", "Building innkeeper", 1432, 35.2, 46.9, 35.5, 48.5);
        Map("Loch Modan", "Building Vidra Heartstove", 1432, 35.2, 46.9, 34.8, 49.1);

        World2("Orgrimmar", "Z Orgrimmar to vendor", 1618.5112, -4433.742, 0, 1633.98, -4439.37, 15.51, uiMap: 1454);

        Map("Redridge Mountains", "Grave to North", 1433, 30, 60, 54.3, 43.1);
        Map("Silithus", "Inn to AQ", 1451, 51.4, 37.8, 29, 92);
        Map("Tanaris", "FP to ZF", 1446, 51.0, 29.3, 38.7, 20.1);

        World("Teldrassil", "Teldrassil to vendor", 10477.419, 659.36426, 1326.2318, 10442.9, 783.989, 1337.37, 1, withReverse: true);
        World("The Barrens", "Search Barrens", -896, -3770, 11, -441, -2596, 96, 1, withReverse: true);

        Map("Zul'Drak", "Stairs", 121, 40.4058, 63.024403, 40.3816, 64.259705);

        return [.. r];
    }
}
