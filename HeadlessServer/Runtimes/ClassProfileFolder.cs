using BotAgent.Runtime;

namespace HeadlessServer.Runtimes;

/// <summary>The class profiles in this PC's <c>Json/class</c>, named as assignments name them (the agent's <see cref="ListClassProfiles"/>).</summary>
public static class ClassProfileFolder
{
    public const string Extension = ".json";

    public static string[] List()
    {
        string folder = Path.GetFullPath(DataConfig.Load().Class);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(folder, "*" + Extension, SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }
}
