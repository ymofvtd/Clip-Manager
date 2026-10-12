namespace VideoTrayApp;

internal static class ClipBatchShuffler
{
    private sealed record Clip(string Path, TimeSpan Duration, long Length, DateTime LastWriteTime);
    private sealed class Batch
    {
        internal List<Clip> Clips { get; } = [];
        internal TimeSpan Duration { get; set; }
    }

    internal static string Run(string root, TimeSpan limit, ISet<string> extensions,
        IOperationProgress progress, CancellationToken ct, Random? random = null)
    {
        root = Path.GetFullPath(root);
        ActionPreset.ValidateFolder(root);
        if (limit <= TimeSpan.Zero) throw new ArgumentException("Batch duration must be positive.");
        var paths = new List<string>();
        var reusableFolders = new List<string>();
        progress.SetIndeterminate("Scanning all archive folders and loose clips...");
        Scan(root);
        paths.Sort(StringComparer.OrdinalIgnoreCase);
        var clips = new List<Clip>();
        for (int i = 0; i < paths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string path = paths[i];
            progress.Report(i, paths.Count, $"Reading duration {i + 1}/{paths.Count}: {Path.GetFileName(path)}");
            var info = new FileInfo(path);
            long length = info.Length;
            DateTime modified = info.LastWriteTimeUtc;
            using var video = TagLib.File.Create(path);
            TimeSpan duration = video.Properties.Duration;
            if (duration <= TimeSpan.Zero) throw new InvalidDataException($"Cannot read a valid video duration: {path}");
            var clip = new Clip(path, duration, length, modified);
            Verify(clip, path);
            clips.Add(clip);
        }
        ct.ThrowIfCancellationRequested();
        if (clips.Count == 0) return "No supported clips found. No files changed.";

        progress.SetIndeterminate("Mixing every clip into new batches...");
        random ??= Random.Shared;
        for (int i = clips.Count - 1; i > 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            int j = random.Next(i + 1);
            (clips[i], clips[j]) = (clips[j], clips[i]);
        }
        // First-fit packing keeps batches within the limit and fills earlier gaps.
        // Oversized clips cannot fit the limit and each receive their own batch.
        var batches = new List<Batch>();
        foreach (var clip in clips)
        {
            ct.ThrowIfCancellationRequested();
            var batch = batches.FirstOrDefault(b => b.Duration + clip.Duration <= limit);
            if (batch is null) batches.Add(batch = new Batch());
            batch.Clips.Add(clip);
            batch.Duration += clip.Duration;
        }

        reusableFolders.Sort(StringComparer.OrdinalIgnoreCase);
        var destinations = reusableFolders.Take(batches.Count).ToList();
        int nextFolder = 1;
        while (destinations.Count < batches.Count)
        {
            ct.ThrowIfCancellationRequested();
            string folder = Path.Combine(root, $"batch_{nextFolder++:D4}");
            if (!File.Exists(folder) && !Directory.Exists(folder)) destinations.Add(folder);
        }

        // Existing preset transactions own rollback; manual runs get the same protection.
        using var transaction = OperationTransaction.IsActive ? null : new OperationTransaction();
        try
        {
            foreach (var clip in clips) { ct.ThrowIfCancellationRequested(); Verify(clip, clip.Path); }
            foreach (string folder in destinations)
            {
                ct.ThrowIfCancellationRequested();
                if (reusableFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) ActionPreset.ValidateFolder(folder);
                else
                {
                    if (File.Exists(folder) || Directory.Exists(folder))
                        throw new IOException($"The new batch folder is now occupied: {folder}");
                    OperationTransaction.CreateDirectory(folder);
                }
            }
            var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Stage beside each original so even same-name swaps need no copies or overwrites.
            for (int i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                progress.Report(i, clips.Count * 2, $"Staging clip {i + 1}/{clips.Count}...");
                ct.ThrowIfCancellationRequested();
                Verify(clip, clip.Path);
                string temporary = Path.Combine(Path.GetDirectoryName(clip.Path)!, Guid.NewGuid().ToString("N") + ".pending");
                OperationTransaction.Move(clip.Path, temporary);
                staged.Add(clip.Path, temporary);
            }
            int moved = 0;
            for (int i = 0; i < batches.Count; i++)
            {
                string folder = destinations[i];
                int sequence = 1;
                foreach (var clip in batches[i].Clips)
                {
                    progress.Report(clips.Count + moved, clips.Count * 2, $"Filling batch {i + 1}/{batches.Count}: clip {moved + 1}/{clips.Count}...");
                    ct.ThrowIfCancellationRequested();
                    ActionPreset.ValidateFolder(folder);
                    string target;
                    do { target = Path.Combine(folder, $"{sequence++:D4}" + Path.GetExtension(clip.Path)); }
                    while (File.Exists(target) || Directory.Exists(target));
                    Verify(clip, staged[clip.Path]);
                    OperationTransaction.Move(staged[clip.Path], target);
                    moved++;
                }
            }
            ct.ThrowIfCancellationRequested();
            transaction?.Commit(ct);
            progress.Report(clips.Count * 2, clips.Count * 2, "All batches reshuffled.");
            int oversized = clips.Count(c => c.Duration > limit);
            return $"Shuffle Batches completed.\nClips reshuffled: {clips.Count}\nBatch folders: {batches.Count}\n" +
                $"Duration limit: {limit.TotalMinutes:g} minutes\nAll clips retained; no backups or duplicate deletion." +
                (oversized > 0 ? $"\nClips longer than the limit in their own folders: {oversized}" : "") +
                "\nExisting folders reused; unused folders and non-video files kept.";
        }
        catch (Exception ex) when (transaction is not null)
        {
            progress.SetIndeterminate("Restoring the original archive layout...");
            try { transaction.Rollback(); }
            catch (Exception recovery)
            {
                throw new IOException($"{ex.Message}\n\nRecovery needs attention:\n{recovery.Message}", ex);
            }
            if (ex is OperationCanceledException)
                throw new OperationCanceledException("Shuffle cancelled. The original archive layout was restored.", ex, ct);
            throw new IOException($"{ex.Message}\n\nThe original archive layout was restored.", ex);
        }

        void Scan(string folder)
        {
            ct.ThrowIfCancellationRequested();
            ActionPreset.ValidateFolder(folder);
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                ct.ThrowIfCancellationRequested();
                if (extensions.Contains(Path.GetExtension(file))
                    && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) paths.Add(file);
            }
            foreach (string child in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (Path.GetFileName(child).Equals("Backup", StringComparison.OrdinalIgnoreCase)
                    || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                int before = paths.Count;
                Scan(child);
                if (ClipIdentifier.SamePath(folder, root) && (paths.Count > before
                    || System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(child), @"^batch_\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                    reusableFolders.Add(child);
            }
        }
    }

    private static void Verify(Clip clip, string path)
    {
        ActionPreset.ValidateFolder(Path.GetDirectoryName(path)!);
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0
            || info.Length != clip.Length || info.LastWriteTimeUtc != clip.LastWriteTime)
            throw new IOException($"Clip changed during preparation: {path}");
    }
}
