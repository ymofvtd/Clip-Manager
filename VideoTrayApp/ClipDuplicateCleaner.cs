using Microsoft.VisualBasic.FileIO;

namespace VideoTrayApp;

internal sealed record DuplicateClipGroup(ClipIdentity Identity, string Keep, List<string> Duplicates);
internal sealed record DuplicateClipResult(List<DuplicateClipGroup> Groups, List<string> Errors)
{
    internal int DuplicateCount => Groups.Sum(group => group.Duplicates.Count);
}

internal static class ClipDuplicateCleaner
{
    internal static DuplicateClipResult Find(string folder, ISet<string> extensions,
        IOperationProgress progress, CancellationToken ct)
    {
        var errors = new List<string>();
        var bySize = new Dictionary<long, List<string>>();
        var folders = new Stack<string>();
        folders.Push(Path.GetFullPath(folder));
        int scanned = 0;
        progress.SetIndeterminate("Listing clips in folder and subfolders...");
        while (folders.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Skip links, including a linked root, to stay inside the selected tree.
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    continue;
                foreach (var path in Directory.GetFileSystemEntries(current))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            folders.Push(path);
                            continue;
                        }
                        if (!extensions.Contains(Path.GetExtension(path)))
                            continue;
                        long size = new FileInfo(path).Length;
                        if (!bySize.TryGetValue(size, out var paths))
                            bySize[size] = paths = [];
                        paths.Add(path);
                        progress.SetIndeterminate($"Found {++scanned} clips...");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        errors.Add($"{path}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{current}: {ex.Message}");
            }
        }

        return Find(bySize.Values.SelectMany(paths => paths)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase), progress, ct, errors);
    }

    // Explicit batch paths exclude backups and preserve the caller's keeper preference.
    internal static DuplicateClipResult Find(IEnumerable<string> files, IOperationProgress progress,
        CancellationToken ct, List<string>? errors = null)
    {
        errors ??= [];
        var paths = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    sizes[path] = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{path}: {ex.Message}");
            }
        }
        // Unique sizes cannot be exact copies. Read full hashes only for possible duplicates.
        var repeatedSizes = sizes.Values.GroupBy(size => size).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet();
        var candidates = paths.Where(path => sizes.TryGetValue(path, out long size)
            && repeatedSizes.Contains(size)).ToList();
        var byIdentity = new Dictionary<ClipIdentity, List<string>>();
        for (int i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string path = candidates[i];
            progress.Report(i, candidates.Count, $"Comparing hash and duration: {Path.GetFileName(path)}");
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    continue;
                var identity = ClipIdentifier.ReadIdentity(path, ct);
                if (!byIdentity.TryGetValue(identity, out var matchingPaths))
                    byIdentity[identity] = matchingPaths = [];
                matchingPaths.Add(path);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
            }
        }
        ct.ThrowIfCancellationRequested();
        var groups = byIdentity.Where(pair => pair.Value.Count > 1)
            .Select(pair => new DuplicateClipGroup(pair.Key, pair.Value[0], pair.Value.Skip(1).ToList()))
            .OrderBy(group => group.Keep, StringComparer.OrdinalIgnoreCase).ToList();
        return new DuplicateClipResult(groups, errors);
    }

    internal static string Apply(DuplicateClipResult result, IOperationProgress progress, CancellationToken ct,
        bool stopBatchOnError = false)
    {
        int completed = 0, processed = 0;
        bool cancelled = false;
        var errors = new List<string>();
        foreach (var group in result.Groups)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(processed, result.DuplicateCount, $"Verifying kept copy: {Path.GetFileName(group.Keep)}");
                // Hold the kept copy open without write/delete sharing throughout this group.
                // If it has gone missing or changed, none of its duplicates are removed.
                using var keptFile = new FileStream(group.Keep, FileMode.Open, FileAccess.Read, FileShare.Read);
                if ((File.GetAttributes(group.Keep) & FileAttributes.ReparsePoint) != 0
                    || ClipIdentifier.ReadIdentity(group.Keep, ct) != group.Identity)
                    throw new IOException("Kept copy changed since the scan; duplicates left untouched.");

                foreach (var path in group.Duplicates)
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        progress.Report(processed, result.DuplicateCount, $"Verifying duplicate: {Path.GetFileName(path)}");
                        if (ClipIdentifier.SamePath(path, group.Keep)
                            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                            || ClipIdentifier.ReadIdentity(path, ct) != group.Identity)
                            throw new IOException("Duplicate changed since the scan; left untouched.");
                        ct.ThrowIfCancellationRequested();
                        OperationTransaction.Recycle(path);
                        completed++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        errors.Add($"{path}: {ex.Message}");
                    }
                    processed++;
                    progress.Report(processed, result.DuplicateCount, $"Sent to Recycle Bin: {completed}");
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                errors.Add($"{group.Keep}: {ex.Message}");
                processed += group.Duplicates.Count;
            }
        }
        if (stopBatchOnError || OperationTransaction.IsActive)
        {
            ct.ThrowIfCancellationRequested();
            if (errors.Count > 0)
                throw new IOException("Duplicate cleanup failed. Batch backup and moving have not started." +
                    ClipIdentifier.FormatErrors(errors));
        }
        return $"{(cancelled ? "Operation cancelled." : "Operation completed.")}\n\n" +
            $"Sent to Recycle Bin: {completed}\nLeft untouched: {processed - completed}\n" +
            $"Unprocessed: {result.DuplicateCount - processed}\nErrors: {errors.Count}" +
            ClipIdentifier.FormatErrors(errors);
    }
}
