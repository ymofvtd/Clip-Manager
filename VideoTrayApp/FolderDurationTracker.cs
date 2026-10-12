using System.Text.RegularExpressions;

namespace VideoTrayApp;

/// <summary>Tracks independent roots, retaining the root/immediate-child duration marker behavior.</summary>
internal sealed class FolderDurationTracker : IDisposable
{
    private readonly object gate = new();
    private readonly HashSet<string> videoExtensions;
    private readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer timer;
    private List<string> roots = [];
    private bool paused;
    private bool disposed;
    private static readonly Regex MarkerPattern = new(@"^\d+M\d+S\.txt$", RegexOptions.IgnoreCase);

    public FolderDurationTracker(IEnumerable<string> extensions)
    {
        videoExtensions = new(extensions, StringComparer.OrdinalIgnoreCase);
        timer = new System.Threading.Timer(_ => RefreshPending(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void SetFolders(IEnumerable<string> folders)
    {
        var normalized = WatchFolderSettings.Normalize(folders);
        lock (gate)
        {
            if (disposed) return;
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            foreach (var watcher in watchers.Values) watcher.Dispose();
            watchers.Clear();
            pending.Clear();
            roots = normalized;
            foreach (string root in roots)
            {
                FileSystemWatcher? watcher = null;
                try
                {
                    if (!Directory.Exists(root)) continue;
                    watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                            | NotifyFilters.LastWrite | NotifyFilters.Size
                    };
                    watcher.Created += (_, e) => OnChange(root, e);
                    watcher.Deleted += (_, e) => OnChange(root, e);
                    watcher.Changed += (_, e) => OnChange(root, e);
                    watcher.Renamed += (_, e) =>
                    {
                        if (!IsMarker(e.OldFullPath) || !IsMarker(e.FullPath)) Schedule(root);
                    };
                    watcher.Error += (_, _) => Schedule(root);
                    watcher.EnableRaisingEvents = !paused;
                    watchers.Add(root, watcher);
                }
                catch
                {
                    watcher?.Dispose(); // One unavailable folder must not prevent tracking the others.
                }
            }
            ScheduleAll();
        }
    }

    private static bool IsMarker(string path) => MarkerPattern.IsMatch(Path.GetFileName(path));

    private void OnChange(string root, FileSystemEventArgs e)
    {
        // Marker writes must never trigger another duration scan.
        if (!IsMarker(e.FullPath)) Schedule(root);
    }

    private void Schedule(string root)
    {
        lock (gate)
        {
            if (disposed || paused || !roots.Contains(root, StringComparer.OrdinalIgnoreCase)) return;
            pending.Add(root);
            timer.Change(500, Timeout.Infinite);
        }
    }

    private void ScheduleAll()
    {
        if (disposed || paused) return;
        pending.UnionWith(roots);
        timer.Change(500, Timeout.Infinite);
    }

    private void RefreshPending()
    {
        lock (gate)
        {
            if (disposed || paused) return;
            var folders = pending.ToArray();
            pending.Clear();
            // Parent and child roots may overlap; scan each folder path only once per refresh.
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in folders)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    UpdateFolder(root, visited);
                    foreach (string child in Directory.EnumerateDirectories(root)) UpdateFolder(child, visited);
                }
                catch
                {
                    // Keep processing other roots when a folder disappears or cannot be read.
                }
            }
        }
    }

    private void UpdateFolder(string path, HashSet<string> visited)
    {
        if (!visited.Add(path)) return;
        try
        {
            var folder = new DirectoryInfo(path);
            TimeSpan total = TimeSpan.Zero;
            foreach (var file in folder.EnumerateFiles().Where(file => videoExtensions.Contains(file.Extension)))
            {
                try
                {
                    using var video = TagLib.File.Create(file.FullName);
                    total += video.Properties.Duration;
                }
                catch { } // A clip may still be copying; Changed will schedule another scan.
            }
            string? markerName = total > TimeSpan.Zero ? $"{(long)total.TotalMinutes}M{total.Seconds}S.txt" : null;
            foreach (var file in folder.GetFiles("*.txt"))
            {
                if (!IsMarker(file.Name) || file.Name.Equals(markerName, StringComparison.OrdinalIgnoreCase)) continue;
                try { file.Delete(); } catch { }
            }
            if (markerName is not null)
            {
                string markerPath = Path.Combine(path, markerName);
                if (!File.Exists(markerPath)) File.WriteAllText(markerPath, "Total Duration Updated");
            }
        }
        catch { } // Failure in one child must not prevent updating its siblings.
    }

    public void Pause()
    {
        // Acquiring the gate also waits for any scan already in flight to finish.
        lock (gate)
        {
            if (disposed) return;
            paused = true;
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            foreach (var watcher in watchers.Values) watcher.EnableRaisingEvents = false;
            pending.Clear();
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            if (disposed) return;
            paused = false;
            // Recreate watchers in case operations removed/replaced a tracked directory.
            SetFolders(roots);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            timer.Dispose();
            foreach (var watcher in watchers.Values) watcher.Dispose();
            watchers.Clear();
            pending.Clear();
        }
    }
}
