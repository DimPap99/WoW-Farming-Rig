using BotAgent.Runtime;

using Contracts.Bots;
using Contracts.Pathing;

using Microsoft.Extensions.Logging;

using ServiceClients;
using ServiceClients.Bots;
using ServiceClients.Pathing;
using ServiceClients.Sync;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BotAgent.Sync;

/// <summary>
/// Before a run, the authored files it needs, pulled from the Pathing Service into this PC's data
/// folder: navigation is local, data is central (<c>docs/service-architecture.md</c>). The bot then
/// reads them from disk as it always has. A Forever run needs its route, a Classic run the paths its
/// profile names (and the one in place of its first); both need the cost zones of the era of the
/// client this PC runs. A file is fetched only when its hash differs (<see cref="SyncHash"/>: line
/// endings do not count), and a local copy changed since the agent last synced it is kept aside
/// first (<c>.sync/backup</c>). Nothing is ever deleted. With the service down, or none configured,
/// the run uses this PC's copies; only a file the assignment itself names that is not here refuses it.
/// </summary>
/// <param name="fileNames">How this PC's file system compares names; by default case-insensitive on
/// Windows. A test of the Windows behaviour gives it.</param>
public sealed partial class DataSync(IPathingClient? pathing, DataConfig data, IGameProbe probe, TimeProvider clock,
    ILogger<DataSync> logger, StringComparer? fileNames = null) : IRunPreparation
{
    /// <summary>The folder of <see cref="DataConfig.Sync"/> that local changes are kept in, one folder per sync.</summary>
    public const string BackupFolder = "backup";

    /// <summary>How many names a message lists before it says how many more.</summary>
    public const int NamesShown = 3;

    private static readonly StringComparison FileNameComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Fields, not the constructor's parameters: a sync's session reaches them through its DataSync.
    // Two names are one file as the file system compares them.
    private readonly StringComparer fileNames = fileNames ?? (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly IPathingClient? pathing = pathing;
    private readonly DataConfig data = data;
    private readonly TimeProvider clock = clock;
    private readonly ILogger<DataSync> logger = logger;

    public async Task<RunPreparation> PrepareAsync(BotAssignment assignment, CancellationToken token)
    {
        Session session = new(this, SyncState.Load(data.Sync, fileNames), token);
        try
        {
            switch (assignment)
            {
                case { Kind: BotRuntimeKind.Forever, Forever: { } forever }:
                    await session.PullAsync(SyncSets.ForeverRoutes, null, [forever.Route], named: true);
                    break;
                case { Kind: BotRuntimeKind.Classic, Classic: { } classic }:
                    await PullProfileAsync(session, classic);
                    break;
            }

            // The cost zones of the client this PC runs; with none running, the run is refused next anyway.
            if (probe.Probe()?.Era is { } era && DataConfig.Eras.Contains(era))
            {
                await session.PullAllAsync(SyncSets.CostZones, era);
            }
        }
        finally
        {
            session.Save();
        }

        return session.Result();
    }

    // The path in place of the first, then the profile's own and those of its path groups. The
    // profile itself is this PC's: profiles are committed data, not synced.
    private async Task PullProfileAsync(Session session, ClassicAssignment classic)
    {
        if (classic.PathOverride is { } replacement)
        {
            await session.PullAsync(SyncSets.Paths, null, [replacement], named: true);
        }

        string profile = Path.Join(data.Class, classic.ClassProfile);
        if (!File.Exists(profile))
        {
            return;
        }

        if (!ProfilePaths.TryReadFile(profile, out ProfileFiles files, out string error))
        {
            session.Note($"The profile {classic.ClassProfile} could not be read for its paths: {error}.");
            return;
        }

        session.Skip(files.Skipped);
        await session.PullAsync(SyncSets.Paths, null, [.. files.Groups, .. files.Paths], named: false);
        foreach (string group in files.Groups)
        {
            // A group names more paths: read as it is now, pulled or this PC's own.
            if (LocalFile(SyncSets.Paths, null, group) is { } file && ProfilePaths.TryReadFile(file, out ProfileFiles grouped, out _))
            {
                session.Skip(grouped.Skipped);
                await session.PullAsync(SyncSets.Paths, null, grouped.Paths, named: false);
            }
        }
    }

    // Where a file of a set lives on this PC, or null for a name that would leave the set's folder.
    private string? LocalFile(string set, string? era, string name)
    {
        bool named = set == SyncSets.ForeverRoutes ? AssignmentRules.IsFileName(name) : AssignmentRules.IsRelativeName(name);
        if (!named)
        {
            return null;
        }

        string folder = Path.GetFullPath(set switch
        {
            SyncSets.Paths => data.Path,
            SyncSets.ForeverRoutes => data.ForeverRoutes,
            _ => data.RoadOf(era!),
        });
        string file = Path.GetFullPath(Path.Join(folder, name));
        return file.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, FileNameComparison) ? file : null;
    }

    private static string Count(int files)
    {
        return files == 1 ? "1 file" : $"{files} files";
    }

    // The services' reasons start lower case, to follow a comma.
    private static string Sentence(string text)
    {
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string List(List<string> names)
    {
        string shown = string.Join(", ", names.Take(NamesShown));
        return names.Count > NamesShown ? $"{shown} and {names.Count - NamesShown} more" : shown;
    }

    /// <summary>One sync: what it found, what it did, and what a person should know.</summary>
    private sealed class Session(DataSync sync, SyncState state, CancellationToken token)
    {
        // A name that is two files on the service and one here maps to null (known issue 71).
        private readonly Dictionary<(string Set, string? Era), Dictionary<string, SyncFile?>?> manifests = [];
        private readonly Dictionary<string, string> twins = new(sync.fileNames);
        private readonly List<string> ambiguous = [];
        private readonly HashSet<string> seen = new(sync.fileNames);
        private readonly List<string> backups = [];
        private readonly List<string> notOnService = [];
        private readonly List<string> missing = [];
        private readonly List<string> absent = [];
        private readonly List<string> failed = [];
        private readonly List<string> skipped = [];
        private readonly List<string> notes = [];
        private readonly Dictionary<(string Set, string? Era), string> refusedSets = [];
        private string? unreachable;
        private string? refusal;
        private string? backupFolder;
        private int pulled;
        private int upToDate;

        /// <summary>
        /// Brings each of <paramref name="names"/> up to date. A <paramref name="named"/> file is one the
        /// assignment names: the run cannot start without it, so one that is neither pulled nor here refuses it.
        /// </summary>
        public async Task PullAsync(string set, string? era, IEnumerable<string> names, bool named)
        {
            Dictionary<string, SyncFile?>? manifest = await ManifestAsync(set, era);
            foreach (string name in names)
            {
                token.ThrowIfCancellationRequested();
                string key = SyncState.Key(set, era, name);
                if (!seen.Add(key))
                {
                    continue;
                }

                if (sync.LocalFile(set, era, name) is not { } local)
                {
                    skipped.Add(name);
                    continue;
                }

                SyncFile? file = manifest?.GetValueOrDefault(name);
                if (file is null && manifest is not null && manifest.ContainsKey(name))
                {
                    // Which of the two the assignment means cannot be told here: neither is pulled.
                    if (named)
                    {
                        refusal ??= $"The {(set == SyncSets.ForeverRoutes ? "route" : "path")} {name} is two files on the Pathing Service " +
                                    $"({twins[key]}) and one on this PC: rename one of them there.";
                    }
                    else
                    {
                        ambiguous.Add(twins[key]);
                    }

                    continue;
                }

                string? why = file is not null ? await MirrorAsync(set, era, file)
                    : manifest is null ? Unavailable(set, era)
                    : "the Pathing Service does not have it either";
                if (File.Exists(local))
                {
                    if (manifest is not null && file is null)
                    {
                        notOnService.Add(key);
                    }
                }
                else if (named)
                {
                    refusal ??= $"The {(set == SyncSets.ForeverRoutes ? "route" : "path")} {name} is not on this PC, and {why}.";
                }
                else if (manifest is null)
                {
                    absent.Add(key);
                }
                else if (file is null)
                {
                    missing.Add(key);
                }

                // Otherwise the service has it but it could not be fetched: listed among the failures.
            }
        }

        /// <summary>Brings every file of a set up to date; with the service away, this PC's copies stand as they are.</summary>
        public async Task PullAllAsync(string set, string? era)
        {
            if (await ManifestAsync(set, era) is not { } manifest)
            {
                return;
            }

            foreach ((string name, SyncFile? file) in manifest.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                string key = SyncState.Key(set, era, name);
                if (file is null)
                {
                    ambiguous.Add(twins[key]);
                }
                else if (seen.Add(key))
                {
                    await MirrorAsync(set, era, file);
                }
            }
        }

        public void Note(string note)
        {
            notes.Add(note);
        }

        public void Skip(IEnumerable<string> names)
        {
            skipped.AddRange(names);
        }

        public void Save()
        {
            try
            {
                state.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add($"What was synced could not be recorded ({ex.Message}): the next sync keeps these files' local copies aside.");
            }
        }

        public RunPreparation Result()
        {
            // A refusal says why first; that the run would have used this PC's copies no longer matters.
            List<string> parts = [refusal ?? Source()];
            if (backups.Count > 0)
            {
                parts.Add($"Local changes kept aside in {backupFolder}: {List(backups)}.");
            }

            if (notOnService.Count > 0)
            {
                parts.Add($"Not on the Pathing Service, used as this PC has them: {List(notOnService)}.");
            }

            if (failed.Count > 0)
            {
                parts.Add($"Not updated, this PC's copy used: {List(failed)}.");
            }

            if (missing.Count > 0)
            {
                parts.Add($"Neither on the Pathing Service nor on this PC: {List(missing)}.");
            }

            if (absent.Count > 0)
            {
                parts.Add($"Not on this PC: {List(absent)}.");
            }

            if (skipped.Count > 0)
            {
                parts.Add($"Not synced, not a library name: {List([.. skipped.Distinct()])}.");
            }

            if (ambiguous.Count > 0)
            {
                parts.Add($"Not synced, two files on the Pathing Service that are one on this PC: {List([.. ambiguous.Distinct()])}.");
            }

            parts.AddRange(notes);
            return new RunPreparation(refusal is null, string.Join(" ", parts));
        }

        private string Source()
        {
            if (sync.pathing is null)
            {
                return $"No Pathing Service is set ({PathingServiceOptions.UrlKey}): the run uses this PC's copies.";
            }

            if (unreachable is not null)
            {
                return pulled == 0
                    ? $"{Sentence(unreachable)}; the run uses this PC's copies."
                    : $"Pulled {Count(pulled)} from the Pathing Service, then {unreachable}; the rest are this PC's copies.";
            }

            string refused = refusedSets.Count == 0
                ? string.Empty
                : " " + string.Join(" ", refusedSets.Select(set => $"The {set.Key.Set} set was refused ({set.Value}): this PC's copies are used."));
            return (pulled, upToDate) switch
            {
                (0, 0) => $"Nothing to pull from the Pathing Service.{refused}",
                (0, _) => $"Up to date with the Pathing Service ({Count(upToDate)}).{refused}",
                _ => $"Pulled {Count(pulled)} from the Pathing Service, {upToDate} up to date.{refused}",
            };
        }

        // Why a set's files could not be compared: what a refusal of a missing file says.
        private string Unavailable(string set, string? era)
        {
            return sync.pathing is null ? $"no Pathing Service is set ({PathingServiceOptions.UrlKey})"
                : unreachable is not null ? unreachable
                : refusedSets.TryGetValue((set, era), out string? why) ? $"the Pathing Service refused it ({why})"
                : "the Pathing Service did not list it";
        }

        private async Task<Dictionary<string, SyncFile?>?> ManifestAsync(string set, string? era)
        {
            if (sync.pathing is null || unreachable is not null)
            {
                return null;
            }

            if (manifests.TryGetValue((set, era), out Dictionary<string, SyncFile?>? known))
            {
                return known;
            }

            ServiceResult<SyncManifest> got = await sync.pathing.GetSyncManifestAsync(set, era, token);
            Dictionary<string, SyncFile?>? manifest = null;
            // A manifest with no list, or an entry without its name or hash, is not one: the set is refused.
            if (got.Ok && (got.Value.Files is null || Array.Exists(got.Value.Files, file => file?.Name is null || file.Sha256 is null)))
            {
                got = new ServiceResult<SyncManifest>(default, got.Status, "it answered a manifest this agent cannot read");
            }

            if (got.Ok)
            {
                manifest = new Dictionary<string, SyncFile?>(sync.fileNames);
                foreach (SyncFile file in got.Value.Files)
                {
                    if (!manifest.TryGetValue(file.Name, out SyncFile? first))
                    {
                        manifest[file.Name] = file;
                        continue;
                    }

                    // Route.json and route.json on a Linux service are one file on Windows: neither is pulled.
                    string key = SyncState.Key(set, era, file.Name);
                    twins[key] = first is null ? $"{twins[key]}, {file.Name}" : $"{first.Name} and {file.Name}";
                    manifest[file.Name] = null;
                }
            }
            else if (got.Status == 0)
            {
                // Down or unreachable: asking for the other sets would only wait out the same retries.
                unreachable = got.Error;
            }
            else
            {
                refusedSets[(set, era)] = got.Error ?? $"status {got.Status}";
            }

            manifests[(set, era)] = manifest;
            return manifest;
        }

        // Brings one file up to date: null when it is, else why this PC's copy stays as it is.
        private async Task<string?> MirrorAsync(string set, string? era, SyncFile file)
        {
            string key = SyncState.Key(set, era, file.Name);
            if (sync.LocalFile(set, era, file.Name) is not { } local)
            {
                skipped.Add(file.Name);
                return "it is not a library name";
            }

            try
            {
                string? here = File.Exists(local) ? SyncHash.OfFile(local) : null;
                if (here == file.Sha256)
                {
                    state.Record(key, here);
                    upToDate++;
                    return null;
                }

                if (unreachable is not null)
                {
                    return unreachable;
                }

                ServiceResult<byte[]> got = await sync.pathing!.GetSyncFileAsync(set, file.Name, era, token);
                if (!got.Ok)
                {
                    if (got.Status == 0)
                    {
                        unreachable ??= got.Error;
                    }

                    failed.Add($"{key} ({got.Error})");
                    return $"it could not be fetched ({got.Error})";
                }

                // Bytes that are not what the manifest listed (changed since, cut short, or not the file)
                // are not written: this PC's copy stays, and the next sync tries again (known issue 77).
                if (got.Value.Length != file.Bytes || SyncHash.Of(got.Value) != file.Sha256)
                {
                    failed.Add($"{key} (it came back other than listed)");
                    return "it came back other than the Pathing Service listed it";
                }

                // A copy changed on this PC since it was synced, or never synced, is kept before it goes.
                if (here is not null && state.Synced(key) != here)
                {
                    backupFolder ??= Path.Combine(sync.data.Sync, BackupFolder,
                        sync.clock.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                    string kept = Path.Combine(backupFolder, key);
                    Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                    File.Copy(local, kept, overwrite: true);
                    backups.Add(key);
                    LogKept(sync.logger, local, kept);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                AtomicFile.WriteAllBytes(local, got.Value);
                state.Record(key, SyncHash.Of(got.Value));
                pulled++;
                LogPulled(sync.logger, key, got.Value.Length);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{key} ({ex.Message})");
                return $"it could not be written ({ex.Message})";
            }
        }
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pulled {File} ({Bytes} bytes) from the Pathing Service")]
    static partial void LogPulled(ILogger logger, string file, int bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "{File} was changed on this PC: kept in {Kept} before it is replaced")]
    static partial void LogKept(ILogger logger, string file, string kept);

    #endregion
}
