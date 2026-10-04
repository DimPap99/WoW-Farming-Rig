using Core.Forever;

using System;
using System.IO;
using System.Linq;

namespace Frontend;

/// <summary>
/// Where a WoW: Forever run's files are: the repository the server runs from,
/// which is the runner's working directory, the route files the runner reads
/// there, the runner's build, and the folder run logs go to.
/// </summary>
public sealed class ForeverRunFiles
{
    public const string RunnerName = "ForeverControllerTrial";
    private const string SolutionFile = "WoWFarmingRig.sln";

    public ForeverRunFiles(string repositoryRoot)
    {
        RepositoryRoot = repositoryRoot;
        // The runner reads routes from Json under its working directory, the repository.
        RoutesFolder = Path.Combine(repositoryRoot, "Json", "path", ForeverRouteFile.Folder);
    }

    /// <summary>The files of the repository the server runs from, else of the folder above the data root.</summary>
    public static ForeverRunFiles Locate(DataConfig data) =>
        new(FindRepositoryRoot() ?? Path.GetFullPath(Path.Combine(data.Root, "..")));

    public string RepositoryRoot { get; }

    public string RoutesFolder { get; }

    /// <summary>
    /// The runner's build output written last, its own files and the Core it
    /// carries alike, so a rebuild that only changed Core counts; null when
    /// it was never built.
    /// </summary>
    public string? FindRunner()
    {
        string bin = Path.Combine(RepositoryRoot, "Utilities", RunnerName, "bin");
        if (!Directory.Exists(bin))
            return null;
        return Directory.EnumerateFiles(bin, RunnerName + ".exe", SearchOption.AllDirectories)
            .OrderByDescending(exe => Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, "*.dll")
                .Append(exe).Max(File.GetLastWriteTimeUtc))
            .FirstOrDefault();
    }

    /// <summary>
    /// A new log file for a run of <paramref name="routeFile"/>, beside the
    /// live tests' logs; empty when the repository keeps none.
    /// </summary>
    public string NewLogFile(string routeFile)
    {
        string folder = Path.Combine(RepositoryRoot, "local", "logs");
        if (!Directory.Exists(folder))
            return string.Empty;
        return Path.Combine(folder,
            $"web-{Path.GetFileNameWithoutExtension(routeFile)}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    // The folder with the solution, above the server's build output.
    private static string? FindRepositoryRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, SolutionFile)))
                return folder.FullName;
        }
        return null;
    }
}
