using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PPather.Storage;

/// <summary>
/// Maps logical geometry paths to CASC FileDataIDs using a user-supplied
/// wowdev-style "id;path" listfile. The listfile itself is not redistributed.
/// </summary>
public sealed class CascGeometryListfile
{
    private readonly Dictionary<string, uint> ids = new(StringComparer.OrdinalIgnoreCase);

    public int Count => ids.Count;

    public static CascGeometryListfile Load(string path)
    {
        using StreamReader reader = File.OpenText(path);
        return Load(reader);
    }

    public static CascGeometryListfile Load(TextReader reader)
    {
        CascGeometryListfile result = new();
        string? line;
        int lineNumber = 0;

        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            int separator = line.IndexOf(';');
            if (separator <= 0 || separator == line.Length - 1)
                continue;

            ReadOnlySpan<char> name = line.AsSpan(separator + 1).Trim();
            if (!IsGeometryPath(name))
                continue;

            if (!uint.TryParse(line.AsSpan(0, separator), NumberStyles.None,
                    CultureInfo.InvariantCulture, out uint id) || id == 0)
            {
                throw new InvalidDataException($"Invalid FileDataID on listfile line {lineNumber}.");
            }

            string normalized = Normalize(name);
            if (result.ids.TryGetValue(normalized, out uint existing))
            {
                if (existing != id)
                    throw new InvalidDataException($"Conflicting FileDataIDs for {normalized} on line {lineNumber}.");
            }
            else
            {
                result.ids.Add(normalized, id);
            }
        }

        return result;
    }

    public bool TryGetFileDataId(ReadOnlySpan<char> name, out uint id) =>
        ids.TryGetValue(Normalize(name), out id);

    private static bool IsGeometryPath(ReadOnlySpan<char> name) =>
        name.EndsWith(".adt", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".wdt", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".m2", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(ReadOnlySpan<char> name) =>
        name.ToString().Replace('\\', '/');
}
