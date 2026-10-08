using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;

namespace WinAPI;

public static class ExecutablePath
{
    /// <summary>
    /// The folder of <paramref name="process"/>'s executable, asked of WMI for that one process; empty when Windows
    /// does not say (the process ended, or runs as administrator while this does not). It listed every process with
    /// its command line, joined with a second listing, at every probe of the agent's life.
    /// </summary>
    public static string Get(Process process)
    {
        using ManagementObjectSearcher searcher = new(string.Create(CultureInfo.InvariantCulture,
            $"SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = {process.Id}"));
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementBaseObject found in results)
        {
            using (found)
            {
                return found["ExecutablePath"] is string path && Path.GetDirectoryName(path) is { Length: > 0 } folder
                    ? folder
                    : string.Empty;
            }
        }

        return string.Empty;
    }
}
