namespace VideoTrayApp;

internal sealed record NeutralDuplicateGroup(ClipIdentity Identity, List<string> Paths);
internal sealed record DuplicationCopy(string Path, string Side, string Root, string RelativePath,
    long Size, TimeSpan Duration, DateTime LastWriteTime, PhysicalFileId Physical, bool HardLink)
{
    // Shared A/B paths belong to A too, so they must never default to recycling.
    internal bool RecycleByDefault => Side == "B" && !HardLink;
}
internal sealed record DuplicationGroup(ClipIdentity Identity, List<DuplicationCopy> Copies);
internal sealed record DuplicationScan(List<DuplicationGroup> Groups, List<string> Errors);
internal sealed record DuplicationApplyResult(int Recycled, int Untouched, bool Cancelled, List<string> Errors)
{
    internal string Summary => $"{(Cancelled ? "Cancelled." : "Finished.")}\n\nRecycled: {Recycled}\nUntouched: {Untouched}\nErrors: {Errors.Count}" +
        ClipIdentifier.FormatErrors(Errors);
}

internal static class ClipDuplicationSeeker
{
    internal static DuplicationScan Find(string rootA, string rootB, bool recursive, ISet<string> extensions,
        IOperationProgress progress, CancellationToken ct)
    {
        ActionPreset.ValidateFolder(rootA); ActionPreset.ValidateFolder(rootB);
        rootA = Path.GetFullPath(rootA); rootB = Path.GetFullPath(rootB);
        var errors = new List<string>();
        var a = ArchivePaths.Videos([rootA], extensions, errors, ct, recursive: recursive).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var b = ArchivePaths.Videos([rootB], extensions, errors, ct, recursive: recursive).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sizeGroups = new Dictionary<long, List<string>>();
        foreach (string path in a.Concat(b).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                long size = new FileInfo(path).Length;
                if (!sizeGroups.TryGetValue(size, out var paths)) sizeGroups[size] = paths = [];
                paths.Add(path);
            }
            catch (Exception ex) { errors.Add($"{path}: {ex.Message}"); }
        }
        var candidates = sizeGroups.Values.Where(paths => paths.Count > 1 && paths.Any(a.Contains) && paths.Any(b.Contains)).SelectMany(p => p);
        var neutral = ClipDuplicateCleaner.FindNeutral(candidates, progress, ct, errors);
        var groups = new List<DuplicationGroup>();
        foreach (var group in neutral)
        {
            ct.ThrowIfCancellationRequested();
            if (!group.Paths.Any(a.Contains) || !group.Paths.Any(b.Contains)) continue;
            var copies = new List<DuplicationCopy>();
            foreach (string path in group.Paths)
            {
                try
                {
                    var physical = PhysicalFile.Read(path);
                    bool inA = a.Contains(path), inB = b.Contains(path);
                    string root = inA ? rootA : rootB;
                    copies.Add(new(path, inA && inB ? "A / B (one path)" : inA ? "A" : "B", root,
                        Path.GetRelativePath(root, path), group.Identity.Size, group.Identity.Duration,
                        File.GetLastWriteTimeUtc(path), physical, physical.Links > 1));
                }
                catch (Exception ex) { errors.Add($"{path}: {ex.Message}"); }
            }
            // One underlying file seen through overlapping roots/aliases is never two copies.
            if (copies.Select(c => (c.Physical.Volume, c.Physical.Index)).Distinct().Count() < 2) continue;
            if (copies.Any(c => a.Contains(c.Path)) && copies.Any(c => b.Contains(c.Path))) groups.Add(new(group.Identity, copies));
        }
        return new(groups, errors);
    }

    internal static DuplicationApplyResult Apply(DuplicationScan scan, ISet<string> selected,
        IOperationProgress progress, CancellationToken ct, Action<string>? recycle = null)
    {
        var errors = new List<string>();
        int completed = 0;
        bool cancelled = false;
        // Validate the entire selection before changing any group.
        if (selected.Any(path => !scan.Groups.Any(g => g.Copies.Any(c => c.Path == path))))
            throw new ArgumentException("Selection contains a path outside this review.");
        foreach (var group in scan.Groups.Where(g => g.Copies.Any(c => selected.Contains(c.Path))))
            if (group.Copies.All(c => selected.Contains(c.Path)) || group.Copies.Any(c => selected.Contains(c.Path) && c.HardLink))
                throw new ArgumentException("Keep at least one copy in every group. Hard-link aliases must be kept.");
        foreach (var group in scan.Groups)
        {
            var targets = group.Copies.Where(c => selected.Contains(c.Path)).ToList();
            if (targets.Count == 0) continue;
            var keeper = group.Copies.First(c => !selected.Contains(c.Path));
            try
            {
                ct.ThrowIfCancellationRequested();
                progress.SetIndeterminate($"Verifying kept copy: {keeper.Path}");
                ArchivePaths.RejectLinks(keeper.Path);
                using var kept = new FileStream(keeper.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (PhysicalFile.Read(kept.SafeFileHandle) != keeper.Physical || ClipIdentifier.ReadIdentity(keeper.Path, ct) != group.Identity)
                    throw new IOException("Kept copy changed; group left untouched.");
                foreach (var copy in targets)
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        progress.Report(completed, selected.Count, $"Verifying selected copy: {copy.Path}");
                        ArchivePaths.RejectLinks(copy.Path);
                        using var target = new FileStream(copy.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                        if (PhysicalFile.Read(target.SafeFileHandle) != copy.Physical || PhysicalFile.Read(copy.Path) != copy.Physical
                            || ClipIdentifier.ReadIdentity(copy.Path, ct) != group.Identity)
                            throw new IOException("Selected copy changed; left untouched.");
                        ct.ThrowIfCancellationRequested();
                        (recycle ?? RecycleBin.Recycle)(copy.Path);
                        completed++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { errors.Add($"{copy.Path}: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) { cancelled = true; break; }
            catch (Exception ex) { errors.Add($"{keeper.Path}: {ex.Message}"); }
        }
        return new(completed, selected.Count - completed, cancelled, errors);
    }
}
