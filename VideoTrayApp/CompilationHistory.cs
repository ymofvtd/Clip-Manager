using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoTrayApp;

internal sealed record CompilationHistoryClip(string Name, long Size, string Hash);
internal sealed record CompilationSnapshot(Guid Id, string Name, string Folder, DateTimeOffset SavedAtUtc,
    bool IncludesSubfolders, IReadOnlyList<CompilationHistoryClip> Clips);
internal sealed class CompilationHistoryLog
{
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public string HashAlgorithm { get; set; } = "SHA-256";
    [JsonRequired] public List<CompilationSnapshot> Compilations { get; set; } = [];
}

internal sealed record CompilationScannedClip(RenameFile File, string Hash);
internal sealed record CompilationRecencyClip(CompilationScannedClip Clip, CompilationSnapshot? LastUse, int RecentCompilations);
internal sealed record CompilationRecencyResult(DateTimeOffset CheckedAtUtc, int SavedCompilations,
    IReadOnlyList<CompilationRecencyClip> Clips, IReadOnlyList<string> Errors)
{
    internal int RecentCount => Clips.Count(c => c.RecentCompilations > 0);
}

// The history records explicit saves, independently of compilation sessions and source filenames.
internal sealed class CompilationHistory(string dataFolder)
{
    internal const int RecencyDays = 30;
    private static readonly object Gate = new();
    internal string LogPath { get; } = Path.Combine(dataFolder, "compilation_history.json");

    private CompilationHistoryLog Load()
    {
        try
        {
            CompilationHistoryLog log;
            try
            {
                using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                log = JsonSerializer.Deserialize<CompilationHistoryLog>(stream) ?? throw new InvalidDataException("Empty compilation history.");
            }
            catch (FileNotFoundException) { return new(); }
            catch (DirectoryNotFoundException) { return new(); }
            if (log.Version != 1 || log.HashAlgorithm != "SHA-256" || log.Compilations is null
                || log.Compilations.Any(s => s is null || s.Id == Guid.Empty || string.IsNullOrWhiteSpace(s.Name)
                    || string.IsNullOrWhiteSpace(s.Folder) || !Path.IsPathFullyQualified(s.Folder)
                    || s.SavedAtUtc == default || s.Clips is null || s.Clips.Count == 0
                    || s.Clips.Any(c => c is null || string.IsNullOrWhiteSpace(c.Name) || c.Size < 0
                        || c.Hash is null || c.Hash.Length != 64 || c.Hash.Any(ch => !Uri.IsHexDigit(ch))))
                || log.Compilations.Select(s => s.Id).Distinct().Count() != log.Compilations.Count)
                throw new InvalidDataException("Invalid or unsupported compilation history.");
            return log;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            throw new IOException("Compilation history could not be read. The existing log was retained; restore or repair it before saving or checking recency.\n"
                + LogPath + "\n" + ex.Message, ex);
        }
    }

    internal CompilationSnapshot Save(string folder, string name, bool recursive, IOperationProgress progress,
        CancellationToken ct, TimeProvider? clock = null)
    {
        folder = NormalizeFolder(folder);
        name = name.Trim();
        if (name.Length == 0) name = new DirectoryInfo(folder).Name;
        // Reject unreadable history before spending time hashing, and reload it at commit.
        lock (Gate) Load();
        var scan = Scan(folder, recursive, progress, ct);
        if (scan.Errors.Count > 0)
            throw new IOException("Compilation state was not saved because some clips or folders could not be read. Previous history was retained.\n"
                + string.Join("\n", scan.Errors.Take(12)));
        if (scan.Clips.Count == 0) throw new IOException("No MP4 clips were found. Compilation state was not saved.");
        progress.SetIndeterminate("Verifying compilation contents before saving history...");
        foreach (var clip in scan.Clips) clip.File.Verify(ct);
        var errors = new List<string>();
        var current = ArchivePaths.Videos([folder], VideoFiles.CreateExtensions(), errors, ct, recursive: recursive)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (errors.Count > 0 || !current.SetEquals(scan.Clips.Select(c => c.File.Path)))
            throw new IOException("Compilation contents changed or became unavailable while hashing. Check the folder and save again. Previous history was retained.");
        lock (Gate)
        {
            var log = Load();
            var snapshot = new CompilationSnapshot(Guid.NewGuid(), name, folder, (clock ?? TimeProvider.System).GetUtcNow(), recursive,
                scan.Clips.Select(c => new CompilationHistoryClip(Path.GetRelativePath(folder, c.File.Path), c.File.Length, c.Hash)).ToArray());
            log.Compilations.Add(snapshot);
            ct.ThrowIfCancellationRequested();
            try { AtomicJson.Save(LogPath, log); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new IOException("Compilation state could not be saved. Previous history was retained.\n" + LogPath + "\n" + ex.Message, ex); }
            return snapshot;
        }
    }

    internal CompilationRecencyResult Check(string folder, bool recursive, IOperationProgress progress,
        CancellationToken ct, TimeProvider? clock = null)
    {
        folder = NormalizeFolder(folder);
        CompilationHistoryLog log;
        lock (Gate) log = Load();
        var scan = Scan(folder, recursive, progress, ct);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var cutoff = now.AddDays(-RecencyDays);
        var uses = new Dictionary<string, List<CompilationSnapshot>>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in log.Compilations.AsEnumerable().Reverse().Where(s => s.SavedAtUtc <= now).OrderByDescending(s => s.SavedAtUtc))
        {
            ct.ThrowIfCancellationRequested();
            // Repeated copies in one compilation count as one use of that compilation.
            foreach (string hash in snapshot.Clips.Select(c => c.Hash).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!uses.TryGetValue(hash, out var matches)) uses[hash] = matches = [];
                matches.Add(snapshot);
            }
        }
        var clips = scan.Clips.Select(c =>
        {
            uses.TryGetValue(c.Hash, out var matches);
            return new CompilationRecencyClip(c, matches?.FirstOrDefault(), matches?.Count(s => s.SavedAtUtc >= cutoff) ?? 0);
        }).OrderByDescending(c => c.RecentCompilations > 0).ThenBy(c => c.Clip.File.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        ct.ThrowIfCancellationRequested();
        return new(now, log.Compilations.Count, clips, scan.Errors);
    }

    private static string NormalizeFolder(string folder)
    {
        ActionPreset.ValidateFolder(folder); ArchivePaths.RejectLinks(folder);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }

    private static (List<CompilationScannedClip> Clips, List<string> Errors) Scan(string folder, bool recursive,
        IOperationProgress progress, CancellationToken ct)
    {
        progress.SetIndeterminate("Finding MP4 clips...");
        var errors = new List<string>();
        var paths = ArchivePaths.Videos([folder], VideoFiles.CreateExtensions(), errors, ct, recursive: recursive)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var clips = new List<CompilationScannedClip>();
        for (int i = 0; i < paths.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            string path = paths[i];
            progress.Report(i, paths.Length, "Hashing clip: " + Path.GetFileName(path));
            try
            {
                var file = RenameFile.Read(path, ct);
                using var content = new ArchiveContent(path, new IoRateLimiter(null, new ArchiveClock()), ct);
                string hash = content.HashTo(null, null, ct);
                file.Verify(ct);
                clips.Add(new(file, hash));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { errors.Add(path + ": " + ex.Message); }
            progress.Report(i + 1, paths.Length, "Hashed clip: " + Path.GetFileName(path));
        }
        ct.ThrowIfCancellationRequested();
        return (clips, errors);
    }
}
