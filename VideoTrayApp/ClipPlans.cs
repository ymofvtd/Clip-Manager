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
internal sealed record RenamePlan(ReadOnlyCollection<PlannedChange> Changes)
{
    internal static RenamePlan Analyze(string folder, PresetAction mode, int start, int padding, string prefix,
        int length, ISet<string> extensions, IOperationProgress progress, CancellationToken ct, Random? random = null)
    {
        folder = Path.GetFullPath(folder);
        ActionPreset.ValidateFolder(folder); random ??= Random.Shared;
        if (start < 0 || padding is < 0 or > 64 || length is < 4 or > 64) throw new ArgumentException("Invalid numbering or random-name settings.");
        if (prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Prefix contains an invalid filename character.");
        var paths = Directory.EnumerateFiles(folder).Where(p => !Path.GetFileName(p).StartsWith('.') && extensions.Contains(Path.GetExtension(p)))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (mode == PresetAction.NameByTens) paths.Sort((a, b) => NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)));
        if (mode != PresetAction.NameByTens)
            for (int i = paths.Count - 1; i > 0; i--) { int j = random.Next(i + 1); (paths[i], paths[j]) = (paths[j], paths[i]); }
        var originals = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<PlannedChange>();
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
            changes.Add(new(PlannedFile.Read(paths[i], ct), target));
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
    {
        return PlanExecution.Transaction(progress, ct, () =>
        {
            foreach (var change in Changes) change.File.Verify(ct);
            var originals = Changes.Select(c => c.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var change in Changes)
                if (Directory.Exists(change.Destination) || File.Exists(change.Destination) && !originals.Contains(change.Destination))
                    throw new IOException($"Reviewed destination is now occupied: {change.Destination}");
            var staged = new List<(PlannedChange Change, string Temporary)>();
            foreach (var change in Changes.Where(c => !ClipIdentifier.SamePath(c.File.Path, c.Destination)))
            {
                ct.ThrowIfCancellationRequested(); change.File.Verify(ct);
                string temporary = change.File.Path + "." + Guid.NewGuid().ToString("N") + ".pending";
                OperationTransaction.Move(change.File.Path, temporary); staged.Add((change, temporary));
            }
            int completed = 0;
            foreach (var item in staged)
            {
                ct.ThrowIfCancellationRequested(); progress.Report(completed, staged.Count, "Renaming reviewed files…");
                OperationTransaction.Move(item.Temporary, item.Change.Destination); completed++;
            }
            return $"Renamed: {completed}\nUnchanged: {Changes.Count - completed}\nThe reviewed filename mapping was used. In-process rollback is available on cancellation or failure; forced termination is outside that guarantee.";
        });
    }
}

internal sealed record PrepareBatchPlan(string Source, string Parent, BatchDestination Batch,
    ReadOnlyCollection<PlannedChange> Changes, ReadOnlyCollection<DuplicateClipGroup> DuplicateGroups,
    ReadOnlyCollection<PlannedFile> Guards, TimeSpan StarterDuration, TimeSpan TotalDuration, int StarterCount,
    ReadOnlyCollection<string> StarterPaths, ReadOnlyDictionary<string, TimeSpan> Durations, ReadOnlyCollection<string> Extensions)
{
    internal static PrepareBatchPlan Analyze(ActionPreset preset, string source, ISet<string> extensions,
        IOperationProgress progress, CancellationToken ct, Random? random = null)
    {
        source = Path.GetFullPath(source);
        ActionPreset.ValidateFolder(source);
        var batch = BatchDestinationResolver.Resolve(preset, source);
        var errors = new List<string>();
        var candidates = ArchivePaths.Videos([source], extensions, errors, ct, recursive: preset.Selection == BatchSelection.Archives)
            .Where(p => !p.StartsWith(Path.Combine(source, "Backup") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !p.StartsWith(batch.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !(preset.Selection == BatchSelection.Archives && !ClipIdentifier.SamePath(source, preset.Destination)
                    && p.StartsWith(Path.GetFullPath(preset.Destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Where(p => preset.Selection != BatchSelection.NumberedMp4 || Path.GetExtension(p).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(p).Length > 0 && Path.GetFileNameWithoutExtension(p).All(char.IsAsciiDigit)).ToList();
        if (errors.Count > 0) throw new IOException("Batch scan incomplete." + ClipIdentifier.FormatErrors(errors));
        random ??= Random.Shared;
        for (int i = candidates.Count - 1; i > 0; i--) { int j = random.Next(i + 1); (candidates[i], candidates[j]) = (candidates[j], candidates[i]); }
        var starters = batch.Create ? [] : Directory.EnumerateFiles(batch.Path).Where(p => extensions.Contains(Path.GetExtension(p))).Order(StringComparer.OrdinalIgnoreCase).ToList();
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
            var pool = starters.Concat(selected).Where(p => !deleted.Contains(p)).ToList();
            TimeSpan total = TimeSpan.FromTicks(pool.Sum(p => Duration(p).Ticks));
            while (next < candidates.Count && total < TimeSpan.FromMinutes(preset.DurationMinutes))
            { string path = candidates[next++]; selected.Add(path); pool.Add(path); total += Duration(path); }
            var duplicates = ClipDuplicateCleaner.Find(pool, progress, ct);
            if (duplicates.Errors.Count > 0) throw new IOException("Could not verify the batch." + ClipIdentifier.FormatErrors(duplicates.Errors));
            if (duplicates.DuplicateCount == 0) break;
            groups.AddRange(duplicates.Groups);
            foreach (var path in duplicates.Groups.SelectMany(g => g.Duplicates)) deleted.Add(path);
            selected.RemoveAll(deleted.Contains);
        }
        var occupied = batch.Create ? new HashSet<BigInteger>() : Directory.EnumerateFileSystemEntries(batch.Path)
            .Select(Path.GetFileNameWithoutExtension).Where(n => !string.IsNullOrEmpty(n) && n.All(char.IsAsciiDigit)).Select(n => BigInteger.Parse(n!)).ToHashSet();
        var backups = new HashSet<string>(StringComparer.OrdinalIgnoreCase); BigInteger sequence = 1;
        var changes = new List<PlannedChange>();
        foreach (string path in selected.OrderBy(Duration))
        {
            while (occupied.Contains(sequence)) sequence++;
            string target = Path.Combine(batch.Path, sequence + Path.GetExtension(path)); occupied.Add(sequence++);
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Batch destination collision: " + target);
            string backup = Path.Combine(source, "Backup", Path.GetFileName(path));
            for (int suffix = 1; backups.Contains(backup) || File.Exists(backup) || Directory.Exists(backup); suffix++)
                backup = Path.Combine(source, "Backup", Path.GetFileNameWithoutExtension(path) + "__" + suffix + Path.GetExtension(path));
            backups.Add(backup); changes.Add(new(guards[path], target, backup, Duration(path)));
        }
        return new(source, preset.Destination, batch, changes.AsReadOnly(), groups.AsReadOnly(), guards.Values.ToList().AsReadOnly(), starterDuration,
            TimeSpan.FromTicks(starters.Concat(selected).Where(p => !deleted.Contains(p)).Sum(p => Duration(p).Ticks)), starters.Count,
            starters.AsReadOnly(), new(durations), extensions.ToList().AsReadOnly());
    }
    internal string Execute(IOperationProgress progress, CancellationToken ct) => PlanExecution.Transaction(progress, ct, () =>
    {
        Batch.Validate(Source, Parent);
        if (!Batch.Create && !StarterPaths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(
            Directory.EnumerateFiles(Batch.Path).Where(p => Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))))
            throw new IOException("The reviewed starter folder's videos changed. Analyze again before preparing the batch: " + Batch.Path);
        foreach (var guard in Guards) guard.Verify(ct);
        foreach (var change in Changes)
            if (File.Exists(change.Destination) || Directory.Exists(change.Destination) || File.Exists(change.Backup) || Directory.Exists(change.Backup))
                throw new IOException("A reviewed move or backup destination is now occupied: " + change.Destination);
        if (Batch.Create) OperationTransaction.CreateDirectory(Batch.Path);
        // Freeze the exact recycle set, including refill choices; never rescan to select replacements.
        var duplicates = new DuplicateClipResult(DuplicateGroups.ToList(), []);
        ClipDuplicateCleaner.Apply(duplicates, progress, ct, stopBatchOnError: true);
        if (Changes.Count > 0) OperationTransaction.CreateDirectory(Path.Combine(Source, "Backup"));
        int moved = 0;
        foreach (var change in Changes)
        {
            ct.ThrowIfCancellationRequested(); change.File.Verify(ct);
            progress.Report(moved, Changes.Count, "Backing up and moving " + Path.GetFileName(change.File.Path));
            OperationTransaction.Copy(change.File.Path, change.Backup!);
            OperationTransaction.Move(change.File.Path, change.Destination); moved++;
        }
        return $"Moved and backed up: {moved}\nRecycled: {duplicates.DuplicateCount}\nBatch: {Batch.Path}\nBackup: {Path.Combine(Source, "Backup")}\nStarter filenames preserved. Reviewed selection and destinations were used.";
    });
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
