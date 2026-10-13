namespace VideoTrayApp;

internal static class ClipBatchShuffler
{
    internal sealed record Clip(string Path, TimeSpan Duration, long Length, DateTime LastWriteTime, PhysicalFileId Physical);
    internal sealed class Batch
    {
        internal List<Clip> Clips { get; } = [];
        internal TimeSpan Duration { get; set; }
    }

    internal sealed record Mapping(Clip Clip, string Destination);
    internal sealed record Plan(string Root, TimeSpan Limit, IReadOnlyList<Mapping> Changes,
        IReadOnlyList<string> NewFolders, int BatchCount);

    internal static string Run(string root, TimeSpan limit, ISet<string> extensions,
        IOperationProgress progress, CancellationToken ct, Random? random = null) =>
        Execute(Analyze(root, limit, extensions, progress, ct, random), progress, ct);

    internal static Plan Analyze(string root, TimeSpan limit, ISet<string> extensions,
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
            var clip = new Clip(path, duration, length, modified, PhysicalFile.Read(path));
            Verify(clip, path);
            clips.Add(clip);
        }
        ct.ThrowIfCancellationRequested();
        if (clips.Count == 0) return new(root, limit, Array.AsReadOnly(Array.Empty<Mapping>()), Array.AsReadOnly(Array.Empty<string>()), 0);

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

        var originals = clips.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappings = new List<Mapping>();
        for (int i = 0; i < batches.Count; i++)
        {
            int sequence = 1;
            foreach (var clip in batches[i].Clips.OrderBy(c => c.Duration))
            {
                string target;
                do { target = Path.Combine(destinations[i], $"{sequence++:D4}" + Path.GetExtension(clip.Path)); }
                while (Directory.Exists(target) || File.Exists(target) && !originals.Contains(target));
                mappings.Add(new(clip, target));
            }
        }
        return new(root, limit, mappings.AsReadOnly(), destinations.Where(d => !reusableFolders.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList().AsReadOnly(), batches.Count);

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

    internal static string Execute(Plan plan, IOperationProgress progress, CancellationToken ct)
    {
        if (plan.Changes.Count == 0) return "No supported clips found. No files changed.";
        // Existing preset transactions own rollback; manual runs get the same protection.
        using var transaction = OperationTransaction.IsActive ? null : new OperationTransaction();
        try
        {
            ActionPreset.ValidateFolder(plan.Root);
            foreach (var item in plan.Changes) { ct.ThrowIfCancellationRequested(); Verify(item.Clip, item.Clip.Path); }
            var originals = plan.Changes.Select(c => c.Clip.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in plan.Changes)
                if (Directory.Exists(item.Destination) || File.Exists(item.Destination) && !originals.Contains(item.Destination))
                    throw new IOException($"Reviewed destination is now occupied: {item.Destination}");
            foreach (string folder in plan.NewFolders)
            {
                if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException($"The new batch folder is now occupied: {folder}");
                OperationTransaction.CreateDirectory(folder);
            }
            var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < plan.Changes.Count; i++)
            {
                var clip = plan.Changes[i].Clip;
                progress.Report(i, plan.Changes.Count * 2, $"Staging clip {i + 1}/{plan.Changes.Count}...");
                ct.ThrowIfCancellationRequested(); Verify(clip, clip.Path);
                string temporary = Path.Combine(Path.GetDirectoryName(clip.Path)!, Guid.NewGuid().ToString("N") + ".pending");
                OperationTransaction.Move(clip.Path, temporary); staged.Add(clip.Path, temporary);
            }
            int moved = 0;
            var folders = plan.Changes.Select(c => Path.GetDirectoryName(c.Destination)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var item in plan.Changes)
            {
                progress.Report(plan.Changes.Count + moved, plan.Changes.Count * 2, $"Filling batch {folders.IndexOf(Path.GetDirectoryName(item.Destination)) + 1}/{plan.BatchCount}: clip {moved + 1}/{plan.Changes.Count}...");
                ct.ThrowIfCancellationRequested(); ActionPreset.ValidateFolder(Path.GetDirectoryName(item.Destination)!);
                Verify(item.Clip, staged[item.Clip.Path]);
                OperationTransaction.Move(staged[item.Clip.Path], item.Destination); moved++;
            }
            ct.ThrowIfCancellationRequested(); transaction?.Commit(ct);
            progress.Report(plan.Changes.Count * 2, plan.Changes.Count * 2, "All batches reshuffled.");
            int oversized = plan.Changes.Count(c => c.Clip.Duration > plan.Limit);
            return $"Shuffle Batches completed.\nClips reshuffled: {moved}\nBatch folders: {plan.BatchCount}\n" +
                $"Duration limit: {plan.Limit.TotalMinutes:g} minutes\nAll clips retained; no backups or duplicate deletion." +
                (oversized > 0 ? $"\nClips longer than the limit in their own folders: {oversized}" : "") +
                "\nExisting folders reused; unused folders and non-video files kept.";
        }
        catch (Exception ex) when (transaction is not null)
        {
            progress.SetIndeterminate("Restoring the original archive layout...");
            try { transaction.Rollback(); }
            catch (Exception recovery) { throw new IOException($"{ex.Message}\n\nRecovery needs attention:\n{recovery.Message}", ex); }
            if (ex is OperationCanceledException) throw new OperationCanceledException("Shuffle cancelled. The original archive layout was restored.", ex, ct);
            throw new IOException($"{ex.Message}\n\nThe original archive layout was restored.", ex);
        }
    }

    private static void Verify(Clip clip, string path)
    {
        ActionPreset.ValidateFolder(Path.GetDirectoryName(path)!);
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0
            || info.Length != clip.Length || info.LastWriteTimeUtc != clip.LastWriteTime || PhysicalFile.Read(path) != clip.Physical)
            throw new IOException($"Clip changed during preparation: {path}");
    }
}
