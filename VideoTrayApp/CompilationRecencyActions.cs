namespace VideoTrayApp;

internal static class CompilationRecencyActions
{
    internal static ClipDateActionPlan Prepare(CompilationHistory history, CompilationRecencyResult check,
        ISet<string> selected, string? destination, IOperationProgress progress, CancellationToken ct)
    {
        var clips = check.Clips.Where(c => selected.Contains(c.Clip.File.Path)).ToArray();
        if (clips.Length == 0 || clips.Length != selected.Count || clips.Any(c => c.RecentCompilations == 0))
            throw new ArgumentException("Select recent or scheduled clips from the recency results first.");
        var reservedHashes = history.List(ct).SelectMany(s => s.Clips).Select(c => c.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (clips.Any(c => !reservedHashes.Contains(c.Clip.Hash)))
            throw new IOException("Compilation history changed or expired. Check clips recency again before acting.");
        if (destination is not null)
        {
            destination = Path.GetFullPath(destination);
            ActionPreset.ValidateFolder(destination); ArchivePaths.RejectLinks(destination);
        }
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<PlannedChange>();
        foreach (var clip in clips)
        {
            ct.ThrowIfCancellationRequested();
            var scanned = clip.Clip;
            progress.Report(changes.Count, clips.Length, "Preparing recent clip: " + Path.GetFileName(scanned.File.Path));
            scanned.File.Verify(ct);
            var file = PlannedFile.Read(scanned.File.Path, ct);
            scanned.File.Verify(ct);
            if (!string.Equals(file.Hash, scanned.Hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Clip content changed since the recency check. Check again: " + file.Path);
            string target = "";
            if (destination is not null)
            {
                if (ClipIdentifier.SamePath(Path.GetDirectoryName(file.Path)!, destination))
                    throw new IOException("A selected clip is already in the destination. Choose another folder: " + file.Path);
                target = Path.Combine(destination, Path.GetFileName(file.Path));
                for (int suffix = 1; File.Exists(target) || Directory.Exists(target) || targets.Contains(target); suffix++)
                {
                    ct.ThrowIfCancellationRequested();
                    target = Path.Combine(destination, Path.GetFileNameWithoutExtension(file.Path) + "__" + suffix + Path.GetExtension(file.Path));
                }
                targets.Add(target);
            }
            changes.Add(new(file, target));
        }
        return new(changes.AsReadOnly(), destination);
    }
}
