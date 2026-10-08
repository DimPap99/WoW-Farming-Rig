using Newtonsoft.Json;

using System;
using System.IO;
using System.Numerics;

namespace SharedLib;

/// <summary>
/// What a recorded path's file is, told without loading it: a JSON list whose first entry starts with
/// X, Y or Z (any case, as the bot reads them), or an empty list. The path folder holds the class
/// profiles' path groups too, which Newtonsoft reads as points at 0, 0, 0; the Pathing Service leaves
/// them alone by this, and an agent refuses a run whose path is one before it attaches (known issue 68).
/// </summary>
public static class RecordedPathFile
{
    /// <summary>
    /// Why <paramref name="file"/> is not a recorded path, or null when it is one. Reads only up to the
    /// first entry's first name, as the library looks at every file of the folder; comments are skipped,
    /// as the path groups have them.
    /// </summary>
    public static string? Problem(string file)
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

    /// <summary>
    /// Why <paramref name="file"/> would not load as a recorded path, read whole as the bot reads it
    /// (Newtonsoft, a list of <see cref="Vector3"/>), or null: for the one file a run is about to use, where
    /// a truncated copy must be told too.
    /// </summary>
    public static string? LoadProblem(string file)
    {
        if (Problem(file) is string start)
        {
            return start;
        }

        try
        {
            _ = JsonConvert.DeserializeObject<Vector3[]>(File.ReadAllText(file));
            return null;
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
}
