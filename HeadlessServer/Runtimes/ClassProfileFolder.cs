using BotAgent.Runtime;

namespace HeadlessServer.Runtimes;

/// <summary>The class profiles in this PC's <c>Json/class</c>, named as assignments name them.</summary>
public sealed class ClassProfileFolder : IClassProfileCatalog
{
    public const string Extension = ".json";

    public string[] List()
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
