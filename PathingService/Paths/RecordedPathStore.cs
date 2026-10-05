using Contracts;
using Contracts.Pathing;

using Newtonsoft.Json;

using PathingService.Hosting;

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
/// name and the bot walks. Formats stay exactly as they were, so the bot, BlazorServer and
/// PathingAPI keep reading the same files: points are read the way the bot reads them
/// (Newtonsoft, case-insensitive, a missing Z is 0) and written the way
/// <c>PathController.SavePath</c> wrote them.
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
                (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
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
        if (!PathName.TryResolve(Root, name, out _, out string file, out string error))
        {
            return StoreResult.Invalid(error);
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
        if (!PathName.TryResolve(Root, name, out _, out string file, out string error))
        {
            return StoreResult.Invalid(error);
        }

        if (!File.Exists(file))
        {
            return StoreResult.NotFound;
        }

        File.Delete(file);
        return StoreResult.Done;
    }
}
