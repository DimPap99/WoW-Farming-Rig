using Contracts;

using ServiceClients;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BotAgent.Sync;

/// <summary>
/// What the agent last synced (<c>.sync/state.json</c> in the data root): each file's hash as it was
/// written, or found already up to date. A local copy that no longer matches its entry was changed
/// on this PC since, so it is kept aside before the sync replaces it; a copy that matches is the
/// agent's own and is replaced as it stands.
/// </summary>
public sealed class SyncState
{
    public const string FileName = "state.json";

    public const int CurrentVersion = 1;

    private readonly string file;
    private readonly Dictionary<string, string> files;

    private SyncState(string file, Dictionary<string, string> files)
    {
        this.file = file;
        this.files = files;
    }

    /// <summary>The state kept in <paramref name="folder"/>, or none: a state that cannot be read leaves every local change to be kept aside.</summary>
    /// <param name="keys">How the file system compares names, so a key names a file as the PC opens it; ordinal by default.</param>
    public static SyncState Load(string folder, StringComparer? keys = null)
    {
        string file = Path.Combine(folder, FileName);
        Dictionary<string, string> files = new(keys ?? StringComparer.Ordinal);
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(file), ContractJson.Files)?.Files is { } stored)
            {
                foreach ((string key, string hash) in stored)
                {
                    files[key] = hash;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return new SyncState(file, files);
    }

    /// <summary>A file's key: its set, the era when the set has one, and its name.</summary>
    public static string Key(string set, string? era, string name)
    {
        return era is null ? $"{set}/{name}" : $"{set}/{era}/{name}";
    }

    /// <summary>The hash of what was last written to, or found in, the file of <paramref name="key"/>.</summary>
    public string? Synced(string key)
    {
        return files.GetValueOrDefault(key);
    }

    public void Record(string key, string hash)
    {
        files[key] = hash;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(new Stored(CurrentVersion, new SortedDictionary<string, string>(files, StringComparer.Ordinal)),
            ContractJson.Files));
    }

    private sealed record Stored(int Version, IDictionary<string, string>? Files);
}
