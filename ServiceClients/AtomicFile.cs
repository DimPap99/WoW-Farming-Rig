using System;
using System.IO;

namespace ServiceClients;

/// <summary>
/// Writes a data file so a reader never sees half of it: the bot and the cost-zone watcher read
/// these files while the Pathing Service's stores save them, or an agent's sync replaces them. The
/// temporary name does not end in <c>.json</c>, so watchers filtering on it see only the final rename.
/// </summary>
public static class AtomicFile
{
    public const string TemporaryExtension = ".tmp";

    public static void WriteAllText(string path, string contents)
    {
        Write(path, temporary => File.WriteAllText(temporary, contents));
    }

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        Write(path, temporary => File.WriteAllBytes(temporary, bytes));
    }

    private static void Write(string path, Action<string> write)
    {
        string temporary = $"{path}.{Guid.NewGuid():N}{TemporaryExtension}";
        try
        {
            write(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
