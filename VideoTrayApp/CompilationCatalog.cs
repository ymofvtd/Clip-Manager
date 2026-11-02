namespace VideoTrayApp;

internal sealed record CompilationCatalogClip(RenameFile File, TimeSpan? Duration);
internal sealed record CompilationCatalogResult(IReadOnlyList<CompilationCatalogClip> Clips, IReadOnlyList<string> Errors)
{
    internal TimeSpan TotalDuration => TimeSpan.FromTicks(Clips.Sum(c => c.Duration?.Ticks ?? 0));
}

// Cache only metadata from files with reliable change stamps. Browsing never needs content hashing.
internal sealed class CompilationCatalog
{
    private Dictionary<string, CompilationCatalogClip> cache = new(StringComparer.OrdinalIgnoreCase);

    internal CompilationCatalogResult Read(string folder, CancellationToken ct)
    {
        var clips = new List<CompilationCatalogClip>();
        var errors = new List<string>();
        var next = new Dictionary<string, CompilationCatalogClip>(StringComparer.OrdinalIgnoreCase);
        ActionPreset.ValidateFolder(folder); ArchivePaths.RejectLinks(folder);
        foreach (string path in ArchivePaths.Videos([folder], VideoFiles.CreateExtensions(), errors, ct, recursive: false))
        {
            ct.ThrowIfCancellationRequested();
            RenameFile? file = null;
            try
            {
                file = RenameFile.Read(path, ct);
                if (file.Stamp is not null && cache.TryGetValue(path, out var prior) && prior.File == file && prior.Duration is not null)
                { clips.Add(prior); next[path] = prior; continue; }
                using var content = MonthlyBatchPreparer.Open(path, ct);
                TimeSpan duration = content.Duration();
                file.Verify(ct);
                var clip = new CompilationCatalogClip(file, duration);
                clips.Add(clip); next[path] = clip;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add(path + ": " + ex.Message);
                if (file is not null) clips.Add(new(file, null));
            }
        }
        ct.ThrowIfCancellationRequested();
        cache = next;
        return new(clips.OrderBy(c => c.File.Path, StringComparer.OrdinalIgnoreCase).ToArray(), errors);
    }

    internal static string FormatDuration(TimeSpan duration) => $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
        + (duration.Milliseconds > 0 ? $".{duration.Milliseconds:000}" : "");
}
