using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PPather.Storage;
using TACTSharp;

namespace ForeverCasc;

/// <summary>
/// Read-only CASC source for geometry in a locally installed WoW client.
/// TACTSharp is kept outside PPather so the geometry layer remains container-neutral.
/// </summary>
public sealed class CascGameFileSource : IGameFileSource, ILiquidMetadataSource
{
    public const string SupportedLiquidBuild = "1.60.1.70009";
    private static readonly Dictionary<ushort, (byte SoundBank, byte VertexFormat, byte MaterialFlags)> liquidMetadata = LoadLiquidMetadata();
    private readonly BuildInstance build;
    private readonly CascGeometryListfile listfile;
    private readonly object gate = new();
    private bool disposed;
    private readonly string clientVersion;

    public bool SupportsFileDataIds => true;

    public CascGameFileSource(string installDirectory, string product, string listfilePath, string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(product);
        ArgumentException.ThrowIfNullOrWhiteSpace(listfilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        string install = Path.GetFullPath(installDirectory);
        if (!File.Exists(Path.Combine(install, ".build.info")))
            throw new FileNotFoundException("The WoW install has no .build.info file.", Path.Combine(install, ".build.info"));

        listfile = CascGeometryListfile.Load(listfilePath);
        build = new BuildInstance();
        build.Settings.BaseDir = install;
        build.Settings.Product = product;
        build.Settings.CacheDir = Path.GetFullPath(cacheDirectory);
        build.Settings.TryCDN = false;
        build.Settings.ListfileFallback = false;

        try
        {
            BuildInfo info = new(Path.Combine(install, ".build.info"), build.Settings, build.cdn);
            BuildInfo.AvailableBuild? selected = null;
            foreach (BuildInfo.AvailableBuild entry in info.Entries)
            {
                if (string.Equals(entry.Product, product, StringComparison.OrdinalIgnoreCase))
                {
                    selected = entry;
                    break;
                }
            }

            if (selected is null)
                throw new InvalidDataException($"Product '{product}' was not found in the local .build.info file.");

            clientVersion = selected.Value.Version;
            build.cdn.ProductDirectory = selected.Value.CDNPath;
            build.LoadConfigs(selected.Value.BuildConfig, selected.Value.CDNConfig);
            build.Load();
            if (build.Root is null || build.Encoding is null)
                throw new InvalidDataException("The local CASC root or encoding table did not load.");
        }
        catch
        {
            build.Dispose();
            throw;
        }
    }

    public bool Exists(ReadOnlySpan<char> name)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return listfile.TryGetFileDataId(name, out uint id) && ExistsCore(id);
        }
    }

    public LiquidDescriptor ResolveLiquid(ushort liquidTypeId, ushort vertexFormatOrObjectId)
    {
        if (clientVersion != SupportedLiquidBuild)
            throw new NotSupportedException($"Liquid metadata is pinned to build {SupportedLiquidBuild}, but the installed client is {clientVersion}.");
        if (!liquidMetadata.TryGetValue(liquidTypeId, out var info))
            throw new InvalidDataException($"LiquidType {liquidTypeId} is absent from the {SupportedLiquidBuild} metadata snapshot.");

        byte vertexFormat = vertexFormatOrObjectId < 42
            ? checked((byte)vertexFormatOrObjectId)
            : info.VertexFormat;
        if (liquidTypeId == 2 && vertexFormatOrObjectId >= 42)
            vertexFormat = 2; // ocean depth format in the ADT reader
        if (vertexFormat > 5 || info.SoundBank > 3)
            throw new InvalidDataException($"LiquidType {liquidTypeId} has unsupported format or sound bank.");

        return new(vertexFormat, info.SoundBank is 2 or 3,
            (info.MaterialFlags & 0x2) != 0); // LiquidMaterial VisualOnly
    }

    private static Dictionary<ushort, (byte SoundBank, byte VertexFormat, byte MaterialFlags)> LoadLiquidMetadata()
    {
        using Stream stream = typeof(CascGameFileSource).Assembly.GetManifestResourceStream(
            "ForeverCasc.LiquidMetadata70009.csv")
            ?? throw new InvalidDataException("The Forever liquid metadata resource is missing.");
        using StreamReader reader = new(stream);
        Dictionary<ushort, (byte, byte, byte)> result = [];
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("typeId,", StringComparison.Ordinal))
                continue;
            string[] fields = line.Split(',');
            if (fields.Length != 4)
                throw new InvalidDataException("The Forever liquid metadata has a malformed row.");
            ushort id = ushort.Parse(fields[0], CultureInfo.InvariantCulture);
            byte soundBank = byte.Parse(fields[1], CultureInfo.InvariantCulture);
            byte vertexFormat = byte.Parse(fields[2], CultureInfo.InvariantCulture);
            byte materialFlags = byte.Parse(fields[3], CultureInfo.InvariantCulture);
            if (!result.TryAdd(id, (soundBank, vertexFormat, materialFlags)))
                throw new InvalidDataException($"LiquidType {id} appears twice in the metadata snapshot.");
        }
        if (result.Count == 0)
            throw new InvalidDataException("The Forever liquid metadata snapshot is empty.");
        return result;
    }

    public Stream OpenRead(ReadOnlySpan<char> name)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!listfile.TryGetFileDataId(name, out uint id))
                throw new FileNotFoundException($"Geometry path is absent from the supplied listfile: {name.ToString()}");
            return OpenReadCore(id);
        }
    }

    public bool Exists(uint fileDataId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return ExistsCore(fileDataId);
        }
    }

    public Stream OpenRead(uint fileDataId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return OpenReadCore(fileDataId);
        }
    }

    private bool ExistsCore(uint id) => id != 0 && build.Root!.GetEntriesByFDID(id).Count != 0;

    private MemoryStream OpenReadCore(uint id)
    {
        if (!ExistsCore(id))
            throw new FileNotFoundException($"FileDataID {id} is absent from the local CASC root.");

        // Resolve through the selected build's root and encoding table. A file
        // missing from local archives throws; TryCDN is permanently disabled.
        var file = build.Root!.GetEntriesByFDID(id)[0];
        var encoding = build.Encoding!.FindContentKey(file.md5.AsSpan());
        if (!encoding)
            throw new FileNotFoundException($"FileDataID {id} has no encoding entry in the local build.");

        byte[] bytes = build.OpenFileByEKey(encoding[0], encoding.DecodedFileSize);
        return new MemoryStream(bytes, writable: false);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            build.Dispose();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);
}
