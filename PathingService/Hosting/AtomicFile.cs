using System;
using System.IO;

namespace PathingService.Hosting;

/// <summary>
/// Writes a data file so a reader never sees half of it: the bot and the cost-zone watcher
/// read these files while they are being saved. The temporary name does not end in
/// <c>.json</c>, so watchers filtering on it see only the final rename.
/// </summary>
public static class AtomicFile
{
    public const string TemporaryExtension = ".tmp";

    public static void WriteAllText(string path, string contents)
    {
        string temporary = $"{path}.{Guid.NewGuid():N}{TemporaryExtension}";
        try
        {
            File.WriteAllText(temporary, contents);
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
