using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Numerics;

namespace VideoTrayApp;

internal sealed record PlannedFile(string Path, long Length, DateTime LastWrite, PhysicalFileId Physical, string Hash)
{
    internal static PlannedFile Read(string path, CancellationToken ct)
    {
        ArchivePaths.RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var info = new FileInfo(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024]; int count;
        while ((count = stream.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return new(path, stream.Length, info.LastWriteTimeUtc, PhysicalFile.Read(stream.SafeFileHandle), Convert.ToHexString(hash.GetHashAndReset()));
    }
    internal void Verify(CancellationToken ct)
    {
        if (Read(Path, ct) != this) throw new IOException($"Reviewed file changed or was replaced; analyze again: {Path}");
    }
    internal static PlannedFile ReadExpected(string path, ClipIdentity identity, CancellationToken ct)
    {
        var file = Read(path, ct);
        if (file.Hash != identity.Hash || file.Length != identity.Size)
            throw new IOException("File content changed during analysis. Analyze again before reviewing changes: " + path);
        return file;
    }
}
internal sealed record PlannedChange(PlannedFile File, string Destination, string? Backup = null, TimeSpan? Duration = null);

// Renaming changes directory entries, so its review guard must not read video contents.
// Keep content hashes in PlannedFile for workflows that depend on exact content identity.
internal sealed record RenameFile(string Path, long Length, DateTime Created, DateTime LastWrite,
    PhysicalFileId Physical, ArchiveFileStamp? Stamp)
{
    internal static RenameFile Read(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArchivePaths.RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var handle = stream.SafeFileHandle;
        return new(path, stream.Length, File.GetCreationTimeUtc(handle), File.GetLastWriteTimeUtc(handle),
            PhysicalFile.Read(handle), PhysicalFile.ArchiveStamp(handle, stream.Length));
    }

    internal void Verify(CancellationToken ct)
    {
        if (Read(Path, ct) != this) throw new IOException($"Reviewed file changed or was replaced; analyze again: {Path}");
    }
}

internal sealed record RenameChange(RenameFile File, string Destination);
internal sealed record RenamePlan(ReadOnlyCollection<RenameChange> Changes)
{
    internal static RenamePlan Analyze(string folder, PresetAction mode, int start, int padding, string prefix,
        int length, ISet<string> extensions, IOperationProgress progress, CancellationToken ct, Random? random = null)
    {
        folder = Path.GetFullPath(folder);
        ActionPreset.ValidateFolder(folder); random ??= Random.Shared;
        if (start < 0 || padding is < 0 or > 64 || length is < 4 or > 64) throw new ArgumentException("Invalid numbering or random-name settings.");
        if (prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Prefix contains an invalid filename character.");
        var paths = Directory.EnumerateFiles(folder).Where(p => !Path.GetFileName(p).StartsWith('.') && VideoFiles.IsMp4(p) && extensions.Contains(Path.GetExtension(p)))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (mode == PresetAction.NameByTens) paths.Sort((a, b) => NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)));
        if (mode != PresetAction.NameByTens)
            for (int i = paths.Count - 1; i > 0; i--) { int j = random.Next(i + 1); (paths[i], paths[j]) = (paths[j], paths[i]); }
        var originals = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<RenameChange>();
        for (int i = 0; i < paths.Count; i++)
        {
            ct.ThrowIfCancellationRequested(); progress.Report(i, paths.Count, $"Analyzing {Path.GetFileName(paths[i])}");
            string stem;
            if (mode == PresetAction.ShuffleRandom)
            {
                const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
                int attempts = 0;
                do
                {
                    if (++attempts > 10000) throw new IOException("Random name space is exhausted. Increase the name length.");
                    stem = new string(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
                } while (destinations.Contains(Path.Combine(folder, stem + Path.GetExtension(paths[i]))) || File.Exists(Path.Combine(folder, stem + Path.GetExtension(paths[i]))));
            }
            else stem = prefix + ((BigInteger)start + i * 10).ToString(padding > 0 ? "D" + padding : "D");
            BatchDestinationResolver.ValidateName(stem);
            string target = Path.Combine(folder, stem + Path.GetExtension(paths[i]));
            if (!destinations.Add(target) || Directory.Exists(target) || File.Exists(target) && !originals.Contains(target))
                throw new IOException($"Proposed filename is occupied: {target}");
            changes.Add(new(RenameFile.Read(paths[i], ct), target));
        }
        return new(changes.AsReadOnly());
    }
    private static int NaturalCompare(string first, string second)
    {
        var a = System.Text.RegularExpressions.Regex.Split(first, @"(\d+)");
        var b = System.Text.RegularExpressions.Regex.Split(second, @"(\d+)");
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            int comparison = BigInteger.TryParse(a[i], out var n) && BigInteger.TryParse(b[i], out var m)
                ? n.CompareTo(m) : StringComparer.OrdinalIgnoreCase.Compare(a[i], b[i]);
            if (comparison != 0) return comparison;
        }
        return a.Length.CompareTo(b.Length);
    }
    internal string Execute(IOperationProgress progress, CancellationToken ct)
        => PlanExecution.Transaction(progress, ct, () => Apply(progress, ct));

    internal string Apply(IOperationProgress progress, CancellationToken ct)
    {
        foreach (var change in Changes)
        {
            progress.SetIndeterminate("Checking reviewed file: " + Path.GetFileName(change.File.Path));
            change.File.Verify(ct);
        }
        var originals = Changes.Select(c => c.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var change in Changes)
            if (Directory.Exists(change.Destination) || File.Exists(change.Destination) && !originals.Contains(change.Destination))
                throw new IOException($"Reviewed destination is now occupied: {change.Destination}");
        var staged = new List<(RenameChange Change, string Temporary)>();
        var aliasStamps = new Dictionary<PhysicalFileId, ArchiveFileStamp?>();
        foreach (var change in Changes.Where(c => !ClipIdentifier.SamePath(c.File.Path, c.Destination)))
        {
            progress.Report(staged.Count, Changes.Count, "Staging rename: " + Path.GetFileName(change.File.Path));
            ct.ThrowIfCancellationRequested();
            var reviewed = aliasStamps.TryGetValue(change.File.Physical, out var stamp)
                ? change.File with { Stamp = stamp } : change.File;
            reviewed.Verify(ct);
            string temporary = change.File.Path + "." + Guid.NewGuid().ToString("N") + ".pending";
            OperationTransaction.Move(change.File.Path, temporary); staged.Add((change, temporary));
            // Renaming one hard link updates the shared change time of its other aliases.
            if (change.File.Physical.Links > 1)
                aliasStamps[change.File.Physical] = RenameFile.Read(temporary, ct).Stamp;
        }
        int completed = 0;
        foreach (var item in staged)
        {
            ct.ThrowIfCancellationRequested(); progress.Report(completed, staged.Count, "Renaming reviewed files…");
            OperationTransaction.Move(item.Temporary, item.Change.Destination); completed++;
        }
        return $"Renamed: {completed}\nUnchanged: {Changes.Count - completed}\nThe reviewed filename mapping was used. In-process rollback is available on cancellation or failure; forced termination is outside that guarantee.";
    }
}

internal sealed record PrepareBatchPlan(string Source, string Parent, BatchDestination Batch,
    ReadOnlyCollection<PlannedChange> Changes, ReadOnlyCollection<DuplicateClipGroup> DuplicateGroups,
    ReadOnlyCollection<PlannedFile> Guards, TimeSpan StarterDuration, TimeSpan TotalDuration, int StarterCount,
    ReadOnlyCollection<string> StarterPaths, ReadOnlyDictionary<string, TimeSpan> Durations, ReadOnlyCollection<string> Extensions,
    ReadOnlyCollection<RenameChange> SourceShuffle, ReadOnlyCollection<RenameChange> RemainingShuffle,
    ReadOnlyCollection<PlannedChange> StarterChanges, BatchSelection? Selection)
{
    internal static PrepareBatchPlan Analyze(ActionPreset preset, string source, ISet<string> extensions,
        IOperationProgress progress, CancellationToken ct, Random? random = null)
    {
        source = Path.GetFullPath(source);
        ActionPreset.ValidateFolder(source);
        var batch = BatchDestinationResolver.Resolve(preset, source);
        var candidates = ListCandidates(source, preset.Destination, batch.Path, preset.Selection, extensions, ct);
        return AnalyzeCandidates(source, preset.Destination, batch, TimeSpan.FromMinutes(preset.DurationMinutes),
            candidates, extensions, progress, ct, random, preset.Selection);
    }

    private static List<string> ListCandidates(string source, string parent, string destination, BatchSelection selection,
        ISet<string> extensions, CancellationToken ct)
    {
        var errors = new List<string>();
        var candidates = ArchivePaths.Videos([source], extensions, errors, ct, recursive: selection == BatchSelection.Archives)
            .Where(p => !p.StartsWith(Path.Combine(source, "Backup") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !p.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !(selection == BatchSelection.Archives && !ClipIdentifier.SamePath(source, parent)
                    && p.StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Where(p => selection != BatchSelection.NumberedMp4 || Path.GetExtension(p).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(p).Length > 0 && Path.GetFileNameWithoutExtension(p).All(char.IsAsciiDigit)).ToList();
        if (errors.Count > 0) throw new IOException("Batch scan incomplete." + ClipIdentifier.FormatErrors(errors));
        return candidates;
    }

    internal static PrepareBatchPlan AnalyzeCandidates(string source, string parent, BatchDestination batch, TimeSpan limit,
        IEnumerable<string> paths, ISet<string> extensions, IOperationProgress progress, CancellationToken ct,
        Random? random = null, BatchSelection? selection = null)
    {
        source = Path.GetFullPath(source);
        parent = Path.GetFullPath(parent);
        ActionPreset.ValidateFolder(source);
        batch.Validate(source, parent);
        string backupFolder = Path.Combine(source, "Backup");
        if (Directory.Exists(backupFolder)) ActionPreset.ValidateFolder(backupFolder);
        if (limit <= TimeSpan.Zero) throw new ArgumentException("Batch duration must be positive.");
        var candidates = paths.Where(VideoFiles.IsMp4).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string path in candidates)
            if (!path.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(Path.Combine(source, "Backup") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(batch.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Batch candidate must be inside the source and outside Backup and the batch: " + path);
        random ??= Random.Shared;
        progress.SetIndeterminate("Shuffling source clips before preparing batch...");
        Shuffle(candidates, random, ct);
        var sourceFiles = candidates.ToDictionary(p => p, p => RenameFile.Read(p, ct), StringComparer.OrdinalIgnoreCase);
        var sourceShuffle = Permute(candidates, p => p, sourceFiles);
        var preparedPaths = sourceShuffle.ToDictionary(c => c.File.Path, c => c.Destination, StringComparer.OrdinalIgnoreCase);
        var starters = batch.Create ? [] : Directory.EnumerateFiles(batch.Path).Where(p => VideoFiles.IsMp4(p) && extensions.Contains(Path.GetExtension(p))).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var guards = new Dictionary<string, PlannedFile>(StringComparer.OrdinalIgnoreCase);
        var durations = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        TimeSpan Duration(string path)
        {
            if (durations.TryGetValue(path, out var value)) return value;
            ct.ThrowIfCancellationRequested(); progress.SetIndeterminate("Analyzing batch clip: " + Path.GetFileName(path));
            guards[path] = PlannedFile.Read(path, ct);
            using var video = TagLib.File.Create(path); value = video.Properties.Duration;
            if (value <= TimeSpan.Zero) throw new InvalidDataException("Cannot read a valid duration: " + path);
            durations[path] = value; return value;
        }
        TimeSpan starterDuration = TimeSpan.FromTicks(starters.Sum(p => Duration(p).Ticks));
        var selected = new List<string>(); var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<DuplicateClipGroup>(); int next = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            progress.SetIndeterminate("Preparing clips for duplicate check...");
            var pool = starters.Concat(selected).Where(p => !deleted.Contains(p)).ToList();
            TimeSpan total = TimeSpan.FromTicks(pool.Sum(p => Duration(p).Ticks));
            while (next < candidates.Count && total < limit)
            { string path = candidates[next++]; selected.Add(path); pool.Add(path); total += Duration(path); }
            progress.SetIndeterminate("Checking prepared batch for duplicates...");
            var duplicates = ClipDuplicateCleaner.Find(pool, progress, ct);
            if (duplicates.Errors.Count > 0) throw new IOException("Could not verify the batch." + ClipIdentifier.FormatErrors(duplicates.Errors));
            if (duplicates.DuplicateCount == 0) break;
            groups.AddRange(duplicates.Groups);
            foreach (var path in duplicates.Groups.SelectMany(g => g.Duplicates)) deleted.Add(path);
            selected.RemoveAll(deleted.Contains);
        }
        var starterSet = starters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var occupied = batch.Create ? new HashSet<BigInteger>() : Directory.EnumerateFileSystemEntries(batch.Path).Where(p => !starterSet.Contains(p))
            .Select(Path.GetFileNameWithoutExtension).Where(n => !string.IsNullOrEmpty(n) && n.All(char.IsAsciiDigit)).Select(n => BigInteger.Parse(n!)).ToHashSet();
        var backups = new HashSet<string>(StringComparer.OrdinalIgnoreCase); BigInteger sequence = 1;
        var changes = new List<PlannedChange>();
        var starterChanges = new List<PlannedChange>();
        foreach (string path in starters.Where(p => !deleted.Contains(p)).Concat(selected).OrderBy(Duration))
        {
            while (occupied.Contains(sequence)) sequence++;
            string target = Path.Combine(batch.Path, sequence + Path.GetExtension(path)); occupied.Add(sequence++);
            if (Directory.Exists(target) || File.Exists(target) && !starterSet.Contains(target)) throw new IOException("Batch destination collision: " + target);
            if (starterSet.Contains(path))
            {
                starterChanges.Add(new(guards[path], target, Duration: Duration(path)));
                continue;
            }
            string backup = Path.Combine(source, "Backup", Path.GetFileName(path));
            for (int suffix = 1; backups.Contains(backup) || File.Exists(backup) || Directory.Exists(backup); suffix++)
                backup = Path.Combine(source, "Backup", Path.GetFileNameWithoutExtension(path) + "__" + suffix + Path.GetExtension(path));
            backups.Add(backup); changes.Add(new(guards[path], target, backup, Duration(path)));
        }
        var selectedSet = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = candidates.Where(p => !deleted.Contains(p) && !selectedSet.Contains(p)).ToList();
        Shuffle(remaining, random, ct);
        var remainingShuffle = Permute(remaining, p => preparedPaths[p], sourceFiles);
        return new(source, parent, batch, changes.AsReadOnly(), groups.AsReadOnly(), guards.Values.ToList().AsReadOnly(), starterDuration,
            TimeSpan.FromTicks(starters.Concat(selected).Where(p => !deleted.Contains(p)).Sum(p => Duration(p).Ticks)), starters.Count,
            starters.AsReadOnly(), new(durations), extensions.ToList().AsReadOnly(), sourceShuffle.AsReadOnly(),
            remainingShuffle.AsReadOnly(), starterChanges.AsReadOnly(), selection);
    }

    private static void Shuffle(List<string> paths, Random random, CancellationToken ct)
    {
        for (int i = paths.Count - 1; i > 0; i--)
        { ct.ThrowIfCancellationRequested(); int j = random.Next(i + 1); (paths[i], paths[j]) = (paths[j], paths[i]); }
        ct.ThrowIfCancellationRequested();
    }

    // Shuffle filename assignments within each folder; keep the folder layout and MP4 extension casing.
    private static List<RenameChange> Permute(List<string> order, Func<string, string> currentPath,
        Dictionary<string, RenameFile> files)
    {
        var result = new List<RenameChange>();
        foreach (var folder in order.GroupBy(p => Path.GetDirectoryName(currentPath(p))!, StringComparer.OrdinalIgnoreCase))
        {
            var slots = folder.Select(currentPath).Order(StringComparer.OrdinalIgnoreCase).ToList();
            int index = 0;
            foreach (string path in folder)
                result.Add(new(files[path], Path.Combine(folder.Key,
                    Path.GetFileNameWithoutExtension(slots[index++]) + Path.GetExtension(path))));
        }
        return result;
    }

    internal string Execute(IOperationProgress progress, CancellationToken ct) => OperationTransaction.IsActive
        ? ExecuteCore(progress, ct) : PlanExecution.Transaction(progress, ct, () => ExecuteCore(progress, ct));

    private string ExecuteCore(IOperationProgress progress, CancellationToken ct)
    {
        Batch.Validate(Source, Parent);
        if (Directory.Exists(Path.Combine(Source, "Backup"))) ActionPreset.ValidateFolder(Path.Combine(Source, "Backup"));
        if (Selection is { } selection && !SourceShuffle.Select(c => c.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(ListCandidates(Source, Parent, Batch.Path, selection, Extensions.ToHashSet(StringComparer.OrdinalIgnoreCase), ct)))
            throw new IOException("The reviewed source videos changed. Analyze again before preparing the batch.");
        if (!Batch.Create && !StarterPaths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(
            Directory.EnumerateFiles(Batch.Path).Where(p => VideoFiles.IsMp4(p) && Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))))
            throw new IOException("The reviewed starter folder's videos changed. Analyze again before preparing the batch: " + Batch.Path);
        foreach (var guard in Guards) guard.Verify(ct);
        foreach (var change in Changes.Concat(StarterChanges))
            if (Directory.Exists(change.Destination) || File.Exists(change.Destination) && !StarterPaths.Contains(change.Destination, StringComparer.OrdinalIgnoreCase)
                || change.Backup is not null && (File.Exists(change.Backup) || Directory.Exists(change.Backup)))
                throw new IOException("A reviewed move or backup destination is now occupied: " + change.Destination);
        progress.SetIndeterminate("Shuffling source filenames before preparing batch...");
        new RenamePlan(SourceShuffle).Apply(progress, ct);
        var preparedPaths = SourceShuffle.ToDictionary(c => c.File.Path, c => c.Destination, StringComparer.OrdinalIgnoreCase);
        string PreparedPath(string original) => preparedPaths.GetValueOrDefault(original, original);
        // Windows filename tunneling can change creation times when shuffle destinations reuse names.
        // Capture the metadata after our own renames so the second shuffle can still reject outside edits.
        var remainingFiles = RemainingShuffle.ToDictionary(c => c.File.Path,
            c => RenameFile.Read(PreparedPath(c.File.Path), ct), StringComparer.OrdinalIgnoreCase);
        progress.SetIndeterminate("Source filenames shuffled; preparing reviewed batch...");
        if (Batch.Create) OperationTransaction.CreateDirectory(Batch.Path);
        // Freeze the exact recycle set, including refill choices; never rescan to select replacements.
        var duplicates = new DuplicateClipResult(DuplicateGroups.Select(g => new DuplicateClipGroup(g.Identity,
            PreparedPath(g.Keep), g.Duplicates.Select(PreparedPath).ToList())).ToList(), []);
        ClipDuplicateCleaner.Apply(duplicates, progress, ct, stopBatchOnError: true);
        if (Changes.Count > 0) OperationTransaction.CreateDirectory(Path.Combine(Source, "Backup"));
        int moved = 0;
        foreach (var change in Changes)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(moved, Changes.Count, $"Backing up {Path.GetFileName(change.File.Path)}...");
            (change.File with { Path = PreparedPath(change.File.Path) }).Verify(ct);
            OperationTransaction.Copy(PreparedPath(change.File.Path), change.Backup!);
            progress.Report(moved++, Changes.Count, $"Moving {Path.GetFileName(change.File.Path)}...");
        }
        var targetRenames = Changes.Concat(StarterChanges).Select(change =>
        {
            string path = PreparedPath(change.File.Path);
            (change.File with { Path = path }).Verify(ct);
            return new RenameChange(RenameFile.Read(path, ct), change.Destination);
        }).ToList();
        progress.SetIndeterminate("Numbering the entire target batch from shortest to longest...");
        new RenamePlan(targetRenames.AsReadOnly()).Apply(progress, ct);
        var remainingRenames = RemainingShuffle.Select(change =>
        {
            var current = RenameFile.Read(PreparedPath(change.File.Path), ct);
            var expected = remainingFiles[change.File.Path];
            // Moving another hard-link alias updates the shared change stamp.
            if (expected.Physical.Links > 1) expected = expected with { Stamp = current.Stamp };
            if (current != expected)
                throw new IOException("Source clip changed during preparation: " + current.Path);
            return new RenameChange(current, change.Destination);
        }).ToList();
        progress.SetIndeterminate("Reshuffling remaining source filenames after preparing batch...");
        new RenamePlan(remainingRenames.AsReadOnly()).Apply(progress, ct);
        progress.SetIndeterminate("Source reshuffle completed.");
        return $"Moved and backed up: {moved}\nDuplicates sent to Recycle Bin: {duplicates.DuplicateCount}\nBatch: {Batch.Path}\nBackup: {Path.Combine(Source, "Backup")}\nSource reshuffled before preparation: {SourceShuffle.Count}; after preparation: {RemainingShuffle.Count}.\nEntire batch numbered shortest to longest. Reviewed selection and destinations were used.";
    }
}

internal static class PlanExecution
{
    internal static string Transaction(IOperationProgress progress, CancellationToken ct, Func<string> work)
    {
        using var transaction = new OperationTransaction();
        try
        {
            string result = work(); transaction.Commit(ct);
            return result + (transaction.CleanupWarnings.Count > 0 ? "\n" + string.Join("\n", transaction.CleanupWarnings) : "");
        }
        catch (Exception ex)
        {
            progress.SetIndeterminate("Recovering original files…");
            try { transaction.Rollback(); }
            catch (Exception recovery) { throw new IOException(ex.Message + "\nRecovery needs attention; retained files:\n" + recovery.Message, ex); }
            if (ex is OperationCanceledException) throw new OperationCanceledException("Cancelled. Changes from this run were restored.", ex, ct);
            throw new IOException(ex.Message + "\nChanges from this run were restored.", ex);
        }
    }
}
