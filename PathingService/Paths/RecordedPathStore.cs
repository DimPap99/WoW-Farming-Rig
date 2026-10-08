using Contracts;
using Contracts.Pathing;

using Newtonsoft.Json;

using PathingService.Hosting;

using ServiceClients;

using ServiceHosting;

using SharedLib;
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
/// The store knows a recorded path by how it starts (<see cref="RecordedPathFile.Problem"/>, SharedLib's) and leaves
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

        foreach (string file in Directory.EnumerateFiles(Root, "*" + LibraryName.Extension, SearchOption.AllDirectories))
        {
            string name = PathName.Relative(Root, file);
            if (PathName.IsForeverRoute(name) ||
                (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                RecordedPathFile.Problem(file) is not null)
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

    public ApiResult<RecordedPath> Get(string name)
    {
        if (!PathName.TryResolve(Root, name, out string canonical, out string file, out string error))
        {
            return ApiResult<RecordedPath>.Invalid(error);
        }

        if (!File.Exists(file))
        {
            return ApiResult<RecordedPath>.NotFound;
        }

        if (RecordedPathFile.Problem(file) is string why)
        {
            return ApiResult<RecordedPath>.Invalid(LeftAlone(canonical, why));
        }

        Vector3[]? points;
        try
        {
            points = JsonConvert.DeserializeObject<Vector3[]>(File.ReadAllText(file));
        }
        catch (JsonException ex)
        {
            return ApiResult<RecordedPath>.Unreadable($"{canonical} is not a list of points: {ex.Message}");
        }

        Point3[] result = new Point3[points?.Length ?? 0];
        for (int i = 0; i < result.Length; i++)
        {
            Vector3 p = points![i];
            result[i] = new Point3(p.X, p.Y, p.Z);
        }

        return ApiResult<RecordedPath>.Done(new RecordedPath(canonical, result));
    }

    /// <summary>Creates or replaces <paramref name="name"/> with <paramref name="points"/>, zone map coordinates.</summary>
    public ApiResult Save(string name, Point3[]? points)
    {
        if (!PathName.TryResolve(Root, name, out string canonical, out string file, out string error))
        {
            return ApiResult.Invalid(error);
        }

        if (File.Exists(file) && RecordedPathFile.Problem(file) is string why)
        {
            return ApiResult.Invalid(LeftAlone(canonical, why));
        }

        if (PathName.StoredSpelling(Root, canonical) is string stored)
        {
            return ApiResult.Conflict(PathName.CaseTwinRefusal(canonical, stored));
        }

        if (points is null || points.Length == 0)
        {
            return ApiResult.Invalid("a recorded path needs at least one point");
        }

        if (points.Length > MaxPoints)
        {
            return ApiResult.Invalid($"a recorded path holds at most {MaxPoints} points");
        }

        Vector3[] vectors = new Vector3[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            Point3 p = points[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
            {
                return ApiResult.Invalid($"point {i} is not made of finite numbers");
            }
            vectors[i] = new Vector3(p.X, p.Y, p.Z);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        AtomicFile.WriteAllText(file, StjSerializer.Serialize(vectors, fileOptions));
        return ApiResult.Done;
    }

    public ApiResult Delete(string name)
    {
        if (!PathName.TryResolve(Root, name, out string canonical, out string file, out string error))
        {
            return ApiResult.Invalid(error);
        }

        if (!File.Exists(file))
        {
            return ApiResult.NotFound;
        }

        if (RecordedPathFile.Problem(file) is string why)
        {
            return ApiResult.Invalid(LeftAlone(canonical, why));
        }

        File.Delete(file);
        return ApiResult.Done;
    }

    private static string LeftAlone(string name, string why)
    {
        return $"{name} is not a recorded path, so the library leaves it alone: {why}";
    }
}
