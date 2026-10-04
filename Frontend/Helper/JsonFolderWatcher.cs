namespace Frontend;

public static class JsonFolderWatcher
{
    /// <summary>
    /// Calls <paramref name="changed"/> whenever a json file in <paramref name="folder"/>
    /// (and below it, with <paramref name="includeSubdirectories"/>) is added, removed,
    /// renamed or changed. It is called on a file system thread. The caller disposes
    /// the watcher.
    /// </summary>
    public static FileSystemWatcher Watch(string folder, bool includeSubdirectories, Action changed)
    {
        FileSystemWatcher watcher = new(folder, "*.json")
        {
            NotifyFilter = NotifyFilters.Attributes
                | NotifyFilters.CreationTime
                | NotifyFilters.DirectoryName
                | NotifyFilters.FileName
                | NotifyFilters.LastAccess
                | NotifyFilters.LastWrite
                | NotifyFilters.Security
                | NotifyFilters.Size,
            IncludeSubdirectories = includeSubdirectories
        };

        void OnChanged(object sender, FileSystemEventArgs e)
        {
            changed();
        }

        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnChanged;

        watcher.EnableRaisingEvents = true;
        return watcher;
    }
}
