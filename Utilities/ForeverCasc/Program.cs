using System;
using System.IO;
using ForeverCasc;
using Microsoft.Extensions.Logging.Abstractions;
using SharedLib.Data;
using WowTriangles;

if (args.Length < 3 || args.Length > 5)
{
    Console.Error.WriteLine("Usage: ForeverCasc <install-dir> <geometry-listfile> <file-data-id-or-path|--wdt-azeroth> [product] [cache-dir]");
    return 2;
}

string product = args.Length >= 4 ? args[3] : "wow_classic_beta";
string cache = args.Length >= 5 ? args[4] : Path.Combine(Path.GetTempPath(), "WoWFarmingRig", "forever-casc");

try
{
    using CascGameFileSource source = new(args[0], product, args[1], cache);
    if (args[2] == "--wdt-azeroth")
    {
        ContinentDB.IdToName[0] = "Azeroth";
        MPQTriangleSupplier supplier = new(NullLogger.Instance, source, 0);
        bool hasSampleTile = supplier.HasAdtAt(-8800, -267);
        supplier.Clear();
        if (!hasSampleTile)
            throw new InvalidDataException("Azeroth WDT does not mark sample tile 32_48 present.");
        Console.WriteLine("Azeroth WDT parsed through PPather; sample tile 32_48 is present.");
        return 0;
    }

    using Stream stream = uint.TryParse(args[2], out uint id)
        ? source.OpenRead(id)
        : source.OpenRead(args[2]);

    Console.WriteLine($"{args[2]}: {stream.Length} bytes, readable={stream.CanRead}, seekable={stream.CanSeek}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CASC read failed: {ex.Message}");
    return 1;
}
