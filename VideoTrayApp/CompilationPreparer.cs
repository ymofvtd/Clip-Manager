namespace VideoTrayApp;

internal static class CompilationPreparer
{
    internal static string Add(CompilationSession session, IEnumerable<string> selected, IOperationProgress progress,
        CancellationToken ct, Random? random = null)
    {
        ct.ThrowIfCancellationRequested();
        string folder = session.Folder;
        ActionPreset.ValidateFolder(folder); ArchivePaths.RejectLinks(folder);
        string[] sources = selected.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sources.Length == 0) throw new ArgumentException("Choose one or more MP4 clips to add.");
        foreach (string path in sources)
        {
            VideoFiles.RequireMp4(path); ArchivePaths.RejectLinks(path);
            if (!File.Exists(path)) throw new FileNotFoundException("Selected clip is unavailable.", path);
            if (ArchivePaths.Contains(folder, path)) throw new IOException("This clip is already inside the compilation folder: " + path);
        }
        var existing = Directory.EnumerateFiles(folder).Where(VideoFiles.IsMp4)
            .Order(StringComparer.OrdinalIgnoreCase).Select(p => RenameFile.Read(p, ct)).ToArray();
        var owned = new Dictionary<string, PhysicalFileId>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return PlanExecution.Transaction(progress, ct, () =>
            {
                var paths = existing.Select(f => f.Path).ToList();
                int copied = 0;
                foreach (string path in sources)
                {
                    ct.ThrowIfCancellationRequested(); ArchivePaths.RejectLinks(folder);
                    progress.Report(copied, sources.Length, "Copying compilation clip: " + Path.GetFileName(path));
                    using var source = MonthlyBatchPreparer.Open(path, ct);
                    TimeSpan duration = source.Duration();
                    string temporary = Path.Combine(folder, ".compilation-" + Guid.NewGuid().ToString("N") + ".pending");
                    string hash;
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        owned.Add(temporary, PhysicalFile.Read(output.SafeFileHandle));
                        hash = source.HashTo(output, (done, total) => progress.SetIndeterminate(
                            $"Copying {Path.GetFileName(path)}: {done:N0} / {total:N0} bytes"), ct);
                        output.Flush(true);
                    }
                    using (var copy = new ArchiveContent(temporary, new IoRateLimiter(null, new ArchiveClock()), ct, formatName: path))
                        if (copy.Identity(ct) != new ClipIdentity(source.Length, duration, hash))
                            throw new IOException("Compilation copy verification failed: " + path);
                    string published = Path.ChangeExtension(temporary, Path.GetExtension(path));
                    ct.ThrowIfCancellationRequested();
                    OperationTransaction.Move(temporary, published); paths.Add(published);
                    progress.Report(++copied, sources.Length, "Verified compilation clip: " + Path.GetFileName(path));
                }
                foreach (var file in existing) file.Verify(ct);
                if (!Directory.EnumerateFiles(folder).Where(VideoFiles.IsMp4).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(paths))
                    throw new IOException("Compilation contents changed while copying. Add the clips again.");
                random ??= Random.Shared;
                var ordered = paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
                for (int i = paths.Count - 1; i > 0; i--)
                { int j = random.Next(i + 1); (paths[i], paths[j]) = (paths[j], paths[i]); }
                if (paths.Count > 1 && paths.SequenceEqual(ordered, StringComparer.OrdinalIgnoreCase))
                    (paths[0], paths[1]) = (paths[1], paths[0]);
                var originals = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var changes = new List<RenameChange>();
                long number = 1;
                foreach (string path in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    string target;
                    do { target = Path.Combine(folder, (number++).ToString("D4") + Path.GetExtension(path)); }
                    while (Directory.Exists(target) || File.Exists(target) && !originals.Contains(target));
                    changes.Add(new(RenameFile.Read(path, ct), target));
                }
                progress.SetIndeterminate("Shuffling compilation clips…");
                new RenamePlan(changes.AsReadOnly()).Apply(progress, ct);
                return $"Added and verified: {copied:N0} clips. Shuffled: {paths.Count:N0} clips.\nCompilation: {session.Name}\nFolder: {folder}";
            });
        }
        finally
        {
            // Rollback returns new copies to these exact temporary paths. Never remove unrelated files.
            var errors = new List<string>();
            foreach (var file in owned)
                try
                {
                    ArchivePaths.RejectLinks(file.Key);
                    if (!File.Exists(file.Key)) continue;
                    if (PhysicalFile.Read(file.Key) != file.Value) throw new IOException("Temporary file was replaced; retained.");
                    File.Delete(file.Key);
                }
                catch (Exception ex) { errors.Add(file.Key + ": " + ex.Message); }
            if (errors.Count > 0) throw new IOException("Compilation temporary files need attention:\n" + string.Join("\n", errors));
        }
    }
}
