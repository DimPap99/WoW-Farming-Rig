using Core.GOAP;

using System;
using System.Collections.Generic;

namespace Core;

public interface IBotController
{
    bool IsBotActive { get; }
    string SelectedClassFilename { get; }
    Dictionary<int, string> SelectedPathFilename { get; }
    ClassConfiguration? ClassConfig { get; }
    GoapAgent? GoapAgent { get; }
    RouteInfo? RouteInfo { get; }
    double AvgScreenLatency { get; }
    double AvgNPCLatency { get; }

    event Action? ProfileLoaded;
    event Action? StatusChanged;

    ClassConfiguration ResolveLoadedProfile();

    void ToggleBotStatus();

    void MinimapNodeFound();

    void Shutdown();

    IEnumerable<string> ClassFiles();

    IEnumerable<string> PathFiles();

    bool LoadClassProfile(string classFilename);

    /// <summary>
    /// Loads <paramref name="classFilename"/> with <paramref name="pathFilenames"/> in place of its paths by
    /// index, in one go: a path the profile names and one of these replaces need not exist.
    /// </summary>
    bool LoadClassProfile(string classFilename, Dictionary<int, string> pathFilenames);

    /// <summary>Loads the selected profile again with <paramref name="pathFilenames"/> in place of its paths; false when it did not load.</summary>
    bool LoadPathProfile(Dictionary<int, string> pathFilenames);

    /// <summary>Why the last profile or path did not load, or null after one that did.</summary>
    string? LoadError { get; }

    void OverrideClassConfig(ClassConfiguration classConfig);

    void SaveClassConfig();
}