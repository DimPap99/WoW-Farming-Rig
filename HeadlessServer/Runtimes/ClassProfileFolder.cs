using BotAgent.Runtime;

namespace HeadlessServer.Runtimes;

/// <summary>The class profiles in this PC's <c>Json/class</c>, named as assignments name them (the agent's <see cref="ListClassProfiles"/>).</summary>
public static class ClassProfileFolder
{
    public const string Extension = ".json";

    /// <summary>The profiles in <paramref name="classFolder"/> (<see cref="DataConfig.Class"/>), <c>/</c> between subfolders.</summary>
    public static string[] List(string classFolder)
    {
        string folder = Path.GetFullPath(classFolder);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(folder, "*" + Extension, SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }
}
