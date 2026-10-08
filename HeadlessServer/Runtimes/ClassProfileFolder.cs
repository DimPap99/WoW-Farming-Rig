using BotAgent.Runtime;

using ServiceClients;

namespace HeadlessServer.Runtimes;

/// <summary>The class profiles in this PC's <c>Json/class</c>, named as assignments name them (the agent's <see cref="ListClassProfiles"/>).</summary>
public static class ClassProfileFolder
{
    /// <summary>The profiles in <paramref name="classFolder"/> (<see cref="DataConfig.Class"/>), as a library lists its files.</summary>
    public static string[] List(string classFolder)
    {
        return [.. LibraryName.Files(Path.GetFullPath(classFolder), nested: true).Select(entry => entry.Name)];
    }
}
