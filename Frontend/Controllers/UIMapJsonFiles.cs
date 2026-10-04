using Newtonsoft.Json;

namespace Frontend.Controllers;

/// <summary>
/// A continent's authored cost zones of one kind (roads, danger zones): a
/// folder with one json file per UI map, named by its id.
/// </summary>
internal static class UIMapJsonFiles
{
    public const string ContinentRequired = "continent is required";

    /// <summary>Every map's data in <paramref name="folder"/>, by UI map id; none when there is no folder.</summary>
    public static Dictionary<int, T> ReadAll<T>(string folder) where T : new()
    {
        Dictionary<int, T> result = [];
        if (!Directory.Exists(folder))
            return result;

        foreach (string file in Directory.GetFiles(folder, "*.json"))
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(file), out int uiMapId))
                result[uiMapId] = Read<T>(file);
        }
        return result;
    }

    public static T Read<T>(string file) where T : new()
    {
        return JsonConvert.DeserializeObject<T>(File.ReadAllText(file)) ?? new T();
    }

    /// <summary>Writes the map's file, or deletes it when <paramref name="empty"/>: nothing is authored there any more.</summary>
    public static void Write<T>(string folder, int uiMapId, T data, bool empty)
    {
        string path = Path.Join(folder, uiMapId + ".json");
        if (empty)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        Directory.CreateDirectory(folder);
        File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));
    }
}
