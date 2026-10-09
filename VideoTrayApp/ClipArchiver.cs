namespace VideoTrayApp;

internal sealed record ArchiveResult(int Moved, int Recycled, int Shuffled, bool Cancelled,
    List<string> Errors)
{
    internal string Summary => $"{(Cancelled ? "Archive cancelled." : Errors.Count > 0 ? "Archive stopped." : "Archive completed.")}\n\n" +
        $"Moved: {Moved}\nDuplicates sent to Recycle Bin: {Recycled}\nShuffled: {Shuffled}\nErrors: {Errors.Count}" +
        ClipIdentifier.FormatErrors(Errors);
}

internal static class ClipArchiver
{
    internal static ArchiveResult Run(string source, string destination, ISet<string> extensions,
        IOperationProgress progress, CancellationToken ct)
    {
        int moved = 0, recycled = 0, shuffled = 0;
        var errors = new List<string>();
        DuplicateClipResult? duplicates = null;
        try
        {
            source = Path.GetFullPath(source);
            destination = Path.GetFullPath(destination);
            if (ClipIdentifier.SamePath(source, destination))
                throw new IOException("Source and destination must be different folders.");
            foreach (string folder in new[] { source, destination })
            {
                if (!Directory.Exists(folder))
                    throw new DirectoryNotFoundException(folder);
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Choose a regular folder rather than a junction or symbolic link.");
            }

            ct.ThrowIfCancellationRequested();
            var files = ListClips(source, extensions);
            if (files.Count == 0)
                return new(moved, recycled, shuffled, false, errors);
            // Prefer copies already in the destination when choosing duplicate keepers.
            var existing = ListClips(destination, extensions);
            var incoming = new List<string>();
            foreach (string path in files)
            {
                progress.Report(moved, files.Count, $"Moving {Path.GetFileName(path)}...");
                ct.ThrowIfCancellationRequested();
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Clip became a symbolic link: {path}");
                string target = UniqueDestination(destination, Path.GetFileName(path));
                File.Move(path, target);
                incoming.Add(target);
                moved++;
            }

            progress.SetIndeterminate("Identifying duplicate clips in destination...");
            ct.ThrowIfCancellationRequested();
            duplicates = ClipDuplicateCleaner.Find(existing.Concat(incoming), progress, ct);
            if (duplicates.Errors.Count > 0)
            {
                errors.AddRange(duplicates.Errors);
                return new(moved, recycled, shuffled, false, errors);
            }
            // The existing cleaner rechecks hashes and durations before recycling extras.
            string cleanup = ClipDuplicateCleaner.Apply(duplicates, progress, ct);
            recycled = duplicates.Groups.Sum(group => group.Duplicates.Count(path => !File.Exists(path)));
            ct.ThrowIfCancellationRequested();
            if (recycled != duplicates.DuplicateCount)
                throw new IOException("Duplicate cleanup was incomplete; shuffling has not started.\n" + cleanup);

            var remaining = ListClips(destination, extensions);
            foreach (string path in remaining)
            {
                progress.Report(shuffled, remaining.Count, $"Shuffling {Path.GetFileName(path)}...");
                ct.ThrowIfCancellationRequested();
                string target = UniqueDestination(destination,
                    Guid.NewGuid().ToString("N")[..12] + Path.GetExtension(path));
                File.Move(path, target);
                shuffled++;
            }
            return new(moved, recycled, shuffled, false, errors);
        }
        catch (OperationCanceledException)
        {
            if (duplicates is not null)
                recycled = duplicates.Groups.Sum(group => group.Duplicates.Count(path => !File.Exists(path)));
            return new(moved, recycled, shuffled, true, errors);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            return new(moved, recycled, shuffled, false, errors);
        }
    }

    private static List<string> ListClips(string folder, ISet<string> extensions) =>
        Directory.EnumerateFiles(folder)
            .Where(path => extensions.Contains(Path.GetExtension(path))
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();

    private static string UniqueDestination(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        int suffix = 1;
        while (File.Exists(path) || Directory.Exists(path))
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)}__{suffix++}{Path.GetExtension(name)}");
        return path;
    }
}
