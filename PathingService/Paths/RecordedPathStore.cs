using Contracts;
using Contracts.Pathing;

using Newtonsoft.Json;

using PathingService.Hosting;

using ServiceClients;

using SharedLib.Converters;

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

using StjSerializer = System.Text.Json.JsonSerializer;
using StjOptions = System.Text.Json.JsonSerializerOptions;

namespace PathingService.Paths;

/// <summary>
/// The recorded path library: the files under <see cref="DataConfig.Path"/> that class profiles
/// name and the bot walks. Formats stay exactly as they were, so the bot and BlazorServer keep
/// reading the same files: points are read the way the bot reads them (Newtonsoft,
/// case-insensitive, a missing Z is 0) and written the way the old
/// <c>PathController.SavePath</c> wrote them.
/// <para>
/// The folder also holds files that are not recorded paths: the class profiles' path groups
/// (<c>PathsFilenames</c>, <c>1-8_*.json</c>), which Newtonsoft would read as points at 0, 0, 0.
/// The store knows a recorded path by how it starts (<see cref="NotARecordedPath"/>) and leaves
/// anything else alone: it does not list it, read it as points, overwrite it or delete it.
/// </para>
/// </summary>
public sealed class RecordedPathStore(DataConfig dataConfig)
{
    /// <summary>More points than any recorded path holds; a guard against runaway requests.</summary>
    public const int MaxPoints = 100_000;

    private static readonly StjOptions fileOptions = new()
    {
        Converters = { new Vector3Converter(true) }
    };

    private string Root => dataConfig.Path;

    /// <summary>Every recorded path whose name contains <paramref name="filter"/>, by name.</summary>
    public IReadOnlyList<RecordedPathInfo> List(string? filter)
    {
        List<RecordedPathInfo> result = [];
        if (!Directory.Exists(Root))
        {
            return result;
        }

        foreach (string file in Directory.EnumerateFiles(Root, "*" + PathName.Extension, SearchOption.AllDirectories))
        {
            string name = PathName.Relative(Root, file);
            if (PathName.IsForeverRoute(name) ||
                (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                NotARecordedPath(file) is not null)
            {
                continue;
            }

            FileInfo info = new(file);
            string screenshot = Path.ChangeExtension(file, PathName.ScreenshotExtension);
            result.Add(new RecordedPathInfo(name, info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                File.Exists(screenshot) ? PathName.Relative(Root, screenshot) : null));
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public StoreResult<RecordedPath> Get(string name)
    {
        if (!PathName.TryResolve(Root, name, out string canonical, out string file, out string error))
        {
            return StoreResult<RecordedPath>.Invalid(error);
        }

        if (!File.Exists(file))
        {
            return StoreResult<RecordedPath>.NotFound;
        }

        if (NotARecordedPath(file) is string why)
        {
            return StoreResult<RecordedPath>.Invalid(LeftAlone(canonical, why));
        }

        Vector3[]? points;
        try
        {
            points = JsonConvert.DeserializeObject<Vector3[]>(File.ReadAllText(file));
        }
        catch (JsonException ex)
        {
            return StoreResult<RecordedPath>.Unreadable($"{canonical} is not a list of points: {ex.Message}");
        }

        Point3[] result = new Point3[points?.Length ?? 0];
        for (int i = 0; i < result.Length; i++)
        {
            Vector3 p = points![i];
            result[i] = new Point3(p.X, p.Y, p.Z);
        }

        return StoreResult<RecordedPath>.Done(new RecordedPath(canonical, result));
    }

    /// <summary>Creates or replaces <paramref name="name"/> with <paramref name="points"/>, zone map coordinates.</summary>
    public StoreResult Save(string name, Point3[]? points)
    {
        if (!PathName.TryResolve(Root, name, out string canonical, out string file, out string error))
        {
            return StoreResult.Invalid(error);
        }

        if (File.Exists(file) && NotARecordedPath(file) is string why)
        {
            return StoreResult.Invalid(LeftAlone(canonical, why));
        }

        if (PathName.StoredSpelling(Root, canonical) is string stored)
        {
            return StoreResult.Conflict(PathName.CaseTwinRefusal(canonical, stored));
        }

        if (points is null || points.Length == 0)
        {
            return StoreResult.Invalid("a recorded path needs at least one point");
        }

        if (points.Length > MaxPoints)
        {
            return StoreResult.Invalid($"a recorded path holds at most {MaxPoints} points");
        }

        Vector3[] vectors = new Vector3[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Point3 p = points[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
            {
                return StoreResult.Invalid($"point {i} is not made of finite numbers");
            }
            vectors[i] = new Vector3(p.X, p.Y, p.Z);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        AtomicFile.WriteAllText(file, StjSerializer.Serialize(vectors, fileOptions));
        return StoreResult.Done;
    }

    public StoreResult Delete(string name)
    {
        if (!PathName.TryResolve(Root, name, out string canonical, out string file, out string error))
        {
            return StoreResult.Invalid(error);
        }

        if (!File.Exists(file))
        {
            return StoreResult.NotFound;
        }

        if (NotARecordedPath(file) is string why)
        {
            return StoreResult.Invalid(LeftAlone(canonical, why));
        }

        File.Delete(file);
        return StoreResult.Done;
    }

    /// <summary>
    /// Why <paramref name="file"/> is not a recorded path, or null when it is one: a JSON list
    /// whose first entry starts with X, Y or Z (any case, as the bot reads them), or an empty
    /// list. Reads only up to that first name, since <see cref="List"/> looks at every file in
    /// the folder; comments are skipped, as the path groups have them.
    /// </summary>
    public static string? NotARecordedPath(string file)
    {
        try
        {
            using StreamReader text = new(file);
            using JsonTextReader json = new(text);
            if (!NextToken(json))
            {
                return "it is empty";
            }

            if (json.TokenType != JsonToken.StartArray || !NextToken(json))
            {
                return "it is not a list of points";
            }

            if (json.TokenType == JsonToken.EndArray)
            {
                return null;
            }

            if (json.TokenType != JsonToken.StartObject || !NextToken(json) ||
                json.TokenType != JsonToken.PropertyName || json.Value is not string first)
            {
                return "it is not a list of points";
            }

            return IsAxis(first) ? null : $"its entries start with \"{first}\", not X, Y and Z (a class profile's path group?)";
        }
        catch (JsonException ex)
        {
            return $"it is not JSON: {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"it could not be read: {ex.Message}";
        }
    }

    private static bool NextToken(JsonTextReader json)
    {
        while (json.Read())
        {
            if (json.TokenType != JsonToken.Comment)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAxis(string name)
    {
        return name.Equals("X", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Y", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Z", StringComparison.OrdinalIgnoreCase);
    }

    private static string LeftAlone(string name, string why)
    {
        return $"{name} is not a recorded path, so the library leaves it alone: {why}";
    }
}
