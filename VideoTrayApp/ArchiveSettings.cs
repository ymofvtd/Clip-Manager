using System.Text.Json;

namespace VideoTrayApp;

internal enum ArchiveSpeed { Slow, Fast }

internal sealed class ArchiveSettings
{
    public List<string> Sources { get; set; } = [];
    public string Destination { get; set; } = "";
    public bool WatchContinuously { get; set; }
    public bool VerifyExistingContent { get; set; }
    public ArchiveSpeed Speed { get; set; } = ArchiveSpeed.Slow;
    public int IntervalSeconds { get; set; } = 120;
    public int? MegabytesPerSecond { get; set; } = 20;
    public int SettlingSeconds { get; set; } = 10;
    public int ReconcileSeconds { get; set; } = 300;

    internal ArchiveSettings Clone() => JsonSerializer.Deserialize<ArchiveSettings>(JsonSerializer.Serialize(this))!;
    internal void Validate(bool requireAvailable = true)
    {
        if (Sources is null || Sources.Count == 0 || Sources.Any(string.IsNullOrWhiteSpace)
            || string.IsNullOrWhiteSpace(Destination)) throw new ArgumentException("Choose sources and an archive destination.");
        if (!Enum.IsDefined(Speed) || IntervalSeconds < 0 || SettlingSeconds < 0 || ReconcileSeconds < 1
            || MegabytesPerSecond is <= 0) throw new ArgumentException("Archive timing and speed settings are invalid.");
        if (WatchContinuously && VerifyExistingContent)
            throw new ArgumentException("Full content rechecks run once. Choose Run once to recheck all clip content.");
        Destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Destination));
        Sources = Sources.Select(source => Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string source in Sources)
            if (ArchivePaths.Contains(source, Destination) || ArchivePaths.Contains(Destination, source))
                throw new IOException("Archive destination and sources must not contain one another.");
        foreach (string folder in Sources.Append(Destination))
        {
            ArchivePaths.RejectLinks(folder);
            if (requireAvailable) ActionPreset.ValidateFolder(folder);
        }
    }
    internal static ArchiveSettings Load(string folder) => AtomicJson.Load<ArchiveSettings>(Path.Combine(folder, "archive_settings.json")) ?? new();
    internal void Save(string folder) => AtomicJson.Save(Path.Combine(folder, "archive_settings.json"), this);
}

internal static class AtomicJson
{
    internal static T? Load<T>(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException($"Empty settings: {path}") : default;
    internal static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal static class ArchivePaths
{
    internal static bool Contains(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root);
        string prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return ClipIdentifier.SamePath(root, path) || Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if ((File.Exists(current.FullName) || Directory.Exists(current.FullName))
                && (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Linked paths are excluded: {path}");
    }

    internal static IEnumerable<string> Videos(IEnumerable<string> roots, ISet<string> extensions,
        List<string> errors, CancellationToken ct, Action? checkpoint = null, bool recursive = true)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new Stack<string>(roots.Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))).Reverse());
        while (folders.TryPop(out string? folder))
        {
            ct.ThrowIfCancellationRequested();
            checkpoint?.Invoke();
            if (!visited.Add(folder)) continue;
            IEnumerator<string>? entries = null;
            try
            {
                RejectLinks(folder);
                entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{folder}: {ex.Message}"); }
            if (entries is null) continue;
            using (entries)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    checkpoint?.Invoke();
                    string path;
                    try { if (!entries.MoveNext()) break; path = entries.Current; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{folder}: {ex.Message}"); break; }
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{path}: {ex.Message}"); continue; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) { if (recursive) folders.Push(path); }
                    else if (extensions.Contains(Path.GetExtension(path))) yield return path;
                }
            }
        }
    }
}
