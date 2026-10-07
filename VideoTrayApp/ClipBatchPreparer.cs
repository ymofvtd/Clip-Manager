namespace VideoTrayApp;

internal static class ClipBatchPreparer
{
    internal static string Prepare(string sourceFolder, string destinationFolder, TimeSpan durationLimit,
        ISet<string> videoExtensions, IOperationProgress progress, CancellationToken ct,
        string numberedExtension = ".mp4", Random? shuffleRandom = null)
    {
        sourceFolder = Path.GetFullPath(sourceFolder);
        destinationFolder = Path.GetFullPath(destinationFolder);
        if (!Directory.Exists(sourceFolder)) throw new DirectoryNotFoundException(sourceFolder);
        if (!Directory.Exists(destinationFolder)) throw new DirectoryNotFoundException(destinationFolder);
        string backupFolder = Path.Combine(sourceFolder, "Backup");
        if (ClipIdentifier.SamePath(sourceFolder, destinationFolder)
            || ClipIdentifier.SamePath(backupFolder, destinationFolder)
            || destinationFolder.StartsWith(backupFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a destination different from the source and outside its Backup folder.");
        if ((File.GetAttributes(sourceFolder) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(destinationFolder) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Batch folders must not be symbolic links or junctions.");

        var files = Directory.EnumerateFiles(sourceFolder)
            .Where(path => string.Equals(Path.GetExtension(path), numberedExtension, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(path).All(char.IsDigit)
                && Path.GetFileNameWithoutExtension(path).Length > 0)
            .OrderBy(path => System.Numerics.BigInteger.Parse(Path.GetFileNameWithoutExtension(path)))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        ct.ThrowIfCancellationRequested();
        progress.SetIndeterminate("Shuffling source clips before preparing batch...");
        var random = shuffleRandom ?? Random.Shared;
        for (int i = files.Count - 1; i > 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            int j = random.Next(i + 1);
            (files[i], files[j]) = (files[j], files[i]);
        }
        var selected = new List<string>();
        int next = 0, recycled = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            progress.SetIndeterminate("Preparing clips for duplicate check...");
            var existing = Directory.EnumerateFiles(destinationFolder)
                .Where(path => videoExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
            selected.RemoveAll(path => !File.Exists(path));
            TimeSpan totalDuration = TimeSpan.Zero;
            foreach (var path in existing.Concat(selected))
                totalDuration += ReadDuration(path);

            // Preserve the existing batch behavior: include the last clip that reaches the limit.
            while (next < files.Count && totalDuration < durationLimit)
            {
                ct.ThrowIfCancellationRequested();
                string path = files[next++];
                selected.Add(path);
                totalDuration += ReadDuration(path);
            }

            progress.SetIndeterminate("Checking prepared batch for duplicates...");
            // Keep destination copies first, then the earliest source clip in the shuffled order.
            var duplicates = ClipDuplicateCleaner.Find(existing.Concat(selected), progress, ct);
            if (duplicates.Errors.Count > 0)
                throw new IOException("Could not verify the batch for duplicates. Backup and moving have not started." +
                    ClipIdentifier.FormatErrors(duplicates.Errors));
            if (duplicates.DuplicateCount == 0)
                break;

            progress.SetIndeterminate($"Recycling {duplicates.DuplicateCount} duplicates before preparing again...");
            ClipDuplicateCleaner.Apply(duplicates, progress, ct, stopBatchOnError: true);
            recycled += duplicates.DuplicateCount;
            // Rescan and refill even when no more source clips remain.
        }

        ct.ThrowIfCancellationRequested();
        if (selected.Count == 0)
            return $"No numbered clips remain to prepare.\nDuplicates sent to Recycle Bin: {recycled}";
        Directory.CreateDirectory(backupFolder);
        int moved = 0;
        var errors = new List<string>();
        foreach (var path in selected)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string name = Path.GetFileName(path);
                string target = Path.Combine(destinationFolder, name);
                if (File.Exists(target) || Directory.Exists(target))
                    throw new IOException($"Destination already contains {name}.");
                string backup = Path.Combine(backupFolder, name);
                for (int suffix = 1; File.Exists(backup) || Directory.Exists(backup); suffix++)
                    backup = Path.Combine(backupFolder, $"{Path.GetFileNameWithoutExtension(name)}__{suffix}{Path.GetExtension(name)}");
                progress.Report(moved, selected.Count, $"Backing up {name}...");
                ct.ThrowIfCancellationRequested();
                File.Copy(path, backup, overwrite: false);
                progress.Report(moved, selected.Count, $"Moving {name}...");
                ct.ThrowIfCancellationRequested();
                File.Move(path, target);
                progress.Report(++moved, selected.Count, $"Moved and backed up: {moved}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
            }
        }
        return $"Batch preparation completed.\nMoved and backed up: {moved}\n" +
            $"Duplicates sent to Recycle Bin: {recycled}\nBackup folder: {backupFolder}" +
            (errors.Count > 0 ? $"\nFailed to back up or move: {errors.Count}. These clips remain in the source folder." +
                ClipIdentifier.FormatErrors(errors) : "");

        TimeSpan ReadDuration(string path)
        {
            ct.ThrowIfCancellationRequested();
            progress.SetIndeterminate($"Reading duration: {Path.GetFileName(path)}");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Linked clip cannot be prepared: {path}");
            using var video = TagLib.File.Create(path);
            return video.Properties.Duration;
        }
    }
}
