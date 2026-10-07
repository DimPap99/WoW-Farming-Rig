using Contracts.Pathing;

using Newtonsoft.Json;

using PathingService.Hosting;
using PathingService.World;

using PPather.Graph;
using PPather.Navmesh;

using ServiceClients;

using SharedLib;

using System;
using System.Collections.Generic;
using System.IO;

using IOPath = System.IO.Path;

namespace PathingService.CostZones;

/// <summary>
/// The authored roads and danger zones under <see cref="DataConfig.Road"/>: one file per uiMap,
/// named by its id, with danger zones in <c>dangerzone/</c>. The layout and the bytes are what
/// the map's old road and danger-zone controllers wrote, so the bot's
/// <see cref="CostZoneLoader"/> keeps reading the same files. Unlike those controllers, a save
/// is checked with the loader's own rules first.
/// </summary>
public sealed class CostZoneStore(DataConfig dataConfig, ClientContinents continents)
{
    /// <summary>A continent's old single road file (<c>{continent}.json</c>) is read as this uiMap.</summary>
    public const int LegacyRoadUiMap = 0;

    public StoreResult<CostZoneSet> Get(string continent)
    {
        if (ContinentProblem(ref continent) is string problem)
        {
            return StoreResult<CostZoneSet>.Invalid(problem);
        }

        string continentDir = IOPath.Join(dataConfig.Road, continent);
        string legacyFile = IOPath.Join(dataConfig.Road, continent + ".json");

        Dictionary<int, RoadZones> roads = [];
        Dictionary<int, DangerZones> danger = [];
        try
        {
            if (Directory.Exists(continentDir))
            {
                foreach ((int uiMapId, RoadData data) in ReadAll<RoadData>(continentDir))
                {
                    roads[uiMapId] = CostZoneMapping.ToContract(data);
                }
            }
            else if (File.Exists(legacyFile))
            {
                roads[LegacyRoadUiMap] = CostZoneMapping.ToContract(Read<RoadData>(legacyFile));
            }

            foreach ((int uiMapId, DangerZoneData data) in ReadAll<DangerZoneData>(DangerDir(continent)))
            {
                danger[uiMapId] = CostZoneMapping.ToContract(data);
            }
        }
        catch (JsonException ex)
        {
            return StoreResult<CostZoneSet>.Unreadable($"a {continent} zone file is not valid: {ex.Message}");
        }

        return StoreResult<CostZoneSet>.Done(new CostZoneSet(continent, roads, danger));
    }

    /// <summary>Replaces one map's roads; no roads deletes its file.</summary>
    public StoreResult SaveRoads(string continent, int uiMapId, RoadZones? zones)
    {
        if ((TargetProblem(ref continent, uiMapId) ?? (zones is null ? "a road set is required" : CostZoneMapping.MissingEntry(zones))) is string problem)
        {
            return StoreResult.Invalid(problem);
        }

        RoadData data = CostZoneMapping.ToModel(zones!);
        if (CostZoneLoader.Problem(data) is string invalid)
        {
            return StoreResult.Invalid(invalid);
        }

        Write(IOPath.Join(dataConfig.Road, continent), uiMapId, data, empty: data.Roads.Length == 0);
        return StoreResult.Done;
    }

    /// <summary>Replaces one map's danger zones; none deletes its file.</summary>
    public StoreResult SaveDanger(string continent, int uiMapId, DangerZones? zones)
    {
        if ((TargetProblem(ref continent, uiMapId) ?? (zones is null ? "a danger zone set is required" : CostZoneMapping.MissingEntry(zones))) is string problem)
        {
            return StoreResult.Invalid(problem);
        }

        if (CostZoneMapping.UnknownMode(zones!) is string mode)
        {
            return StoreResult.Invalid(mode);
        }

        DangerZoneData data = CostZoneMapping.ToModel(zones!);
        if (CostZoneLoader.Problem(data) is string invalid)
        {
            return StoreResult.Invalid(invalid);
        }

        Write(DangerDir(continent), uiMapId, data, empty: data.Circles.Length == 0 && data.Rectangles.Length == 0);
        return StoreResult.Done;
    }

    private string DangerDir(string continent)
    {
        return IOPath.Join(dataConfig.Road, continent, CostZoneLoader.DangerZoneFolder);
    }

    // Continent names become folder names (ClientContinents); the old controllers took any non-empty string.
    private string? ContinentProblem(ref string continent)
    {
        if (continents.Resolve(continent) is not string resolved)
        {
            return continents.Refusal(continent);
        }

        continent = resolved;
        return null;
    }

    private string? TargetProblem(ref string continent, int uiMapId)
    {
        return ContinentProblem(ref continent) ?? (uiMapId < 0 ? "uiMapId must not be negative" : null);
    }

    private static Dictionary<int, T> ReadAll<T>(string folder) where T : new()
    {
        Dictionary<int, T> result = [];
        if (!Directory.Exists(folder))
        {
            return result;
        }

        foreach (string file in Directory.GetFiles(folder, "*.json"))
        {
            if (int.TryParse(IOPath.GetFileNameWithoutExtension(file), out int uiMapId))
            {
                result[uiMapId] = Read<T>(file);
            }
        }

        return result;
    }

    private static T Read<T>(string file) where T : new()
    {
        return JsonConvert.DeserializeObject<T>(File.ReadAllText(file)) ?? new T();
    }

    private static void Write<T>(string folder, int uiMapId, T data, bool empty)
    {
        string path = IOPath.Join(folder, uiMapId + ".json");
        if (empty)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return;
        }

        Directory.CreateDirectory(folder);
        AtomicFile.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));
    }
}
