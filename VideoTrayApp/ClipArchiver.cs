namespace VideoTrayApp;

internal enum ArchiveOutcome { CopiedAndVerified, AlreadyArchived, Pending, Failed }
internal sealed record ArchiveClipResult(ArchiveOutcome Outcome, string Path, string? Message = null, bool DestinationUnavailable = false);
internal sealed record ArchiveResult(int Copied, int AlreadyArchived, int Pending, bool Cancelled, List<string> Errors, int Unchanged = 0)
{
    internal string Summary => $"{(Cancelled ? "Archive cancelled." : "Archive run finished.")}\n\n" +
        $"Copied and verified: {Copied}\nAlready archived (content rechecked): {AlreadyArchived}\nSkipped unchanged (metadata checked): {Unchanged}\nPending: {Pending}\nFailed: {Errors.Count}\nSources left unchanged." +
        ClipIdentifier.FormatErrors(Errors);
}

internal sealed class ClipArchiver
{
    private readonly ArchiveSettings settings;
    private readonly ArchiveIndex index;
    private readonly string indexPath;
    private readonly IoRateLimiter limiter;
    private bool indexDirty;
    internal ClipArchiver(ArchiveSettings settings, string dataFolder, IoRateLimiter limiter)
    {
        this.settings = settings;
        this.limiter = limiter;
        indexPath = ArchiveIndex.IndexPath(dataFolder, settings.Destination);
        index = ArchiveIndex.Load(indexPath, settings.Destination);
    }
    internal void Reconcile(ISet<string> extensions, List<string> errors, CancellationToken ct, Action? checkpoint = null,
        IOperationProgress? progress = null)
    {
        ActionPreset.ValidateFolder(settings.Destination);
        index.RecoverPartials(indexPath);
        indexDirty |= index.Refresh(extensions, limiter, errors, ct, checkpoint, progress, settings.VerifyExistingContent);
        if (indexDirty)
        {
            index.Save(indexPath);
            indexDirty = false;
        }
    }

    internal ArchiveClipResult Process(string source, IOperationProgress progress, CancellationToken ct)
    {
        string? temporary = null;
        ArchiveContent? occupiedContent = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            VideoFiles.RequireMp4(source);
            ActionPreset.ValidateFolder(settings.Destination);
            ArchivePaths.RejectLinks(source);
            DateTime stamp = File.GetLastWriteTimeUtc(source);
            using var content = new ArchiveContent(source, limiter, ct);
            var sourceCreated = new DateTimeOffset(File.GetCreationTimeUtc(source));
            long size = content.Length;
            TimeSpan duration = content.Duration();
            DateTimeOffset? embeddedCreated = content.CreationTime(ct);
            ClipIdentity? identity = null;
            if (index.Entries.Any(entry => entry.Identity.Size == size && entry.Identity.Duration == duration && entry.Locations.Count > 0))
            {
                identity = new(size, duration, content.HashTo(null, (done, total) => ByteProgress("Reading source", source, done, total), ct));
                using var keeper = VerifiedLocation(identity, ct, out string? existing, progress);
                if (existing is not null)
                {
                    RecheckSource();
                    index.Record(identity, existing, source, settings.Sources, content.Stamp, keeper!.Stamp, embeddedCreated, sourceCreated, new DateTimeOffset(stamp));
                    index.Save(indexPath);
                    progress.SetIndeterminate($"Already archived (verified): {source}");
                    return new(ArchiveOutcome.AlreadyArchived, source);
                }
            }
            temporary = Path.Combine(settings.Destination, ".clipsmanager-" + Guid.NewGuid().ToString("N") + ".partial");
            index.OwnedPartials.Add(temporary);
            index.Save(indexPath);
            progress.SetIndeterminate($"Copying {Path.GetFileName(source)}...");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                string hash = content.HashTo(output, (done, total) => progress.Report(
                    (int)(done * 100 / Math.Max(1, total)), 100,
                    $"Copying {Path.GetFileName(source)}: {done:N0}/{total:N0} bytes"), ct, limiter, identity?.Hash);
                identity = new(size, duration, hash);
                ct.ThrowIfCancellationRequested();
                output.Flush(true);
            }
            progress.SetIndeterminate($"Verifying {Path.GetFileName(source)}...");
            using var verification = new ArchiveContent(temporary, limiter, ct, source, allowRename: true);
            if (verification.Identity(ct, (done, total) => ByteProgress("Verifying copy", source, done, total)) != identity)
                throw new IOException("Copied clip failed size, SHA-256 or duration verification.");
            RecheckSource();
            ct.ThrowIfCancellationRequested();
            string target = Path.Combine(settings.Destination, settings.PreserveOriginalName ? Path.GetFileName(source)
                : identity.Hash.ToLowerInvariant() + Path.GetExtension(source).ToLowerInvariant());
            using var duplicateHandle = VerifiedLocation(identity, ct, out string? duplicate, progress);
            bool copied = duplicate is null;
            if (duplicate is not null) target = duplicate;
            else
            {
                int suffix = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    try { File.Move(temporary, target, false); break; }
                    catch (IOException) when (File.Exists(target) || Directory.Exists(target))
                    {
                        if (settings.PreserveOriginalName)
                        {
                            target = Path.Combine(settings.Destination, $"{Path.GetFileNameWithoutExtension(source)}__{++suffix}{Path.GetExtension(source)}");
                            continue;
                        }
                        occupiedContent = new ArchiveContent(target, limiter, ct);
                        if (occupiedContent.Identity(ct) != identity) throw new IOException($"Archive target conflict; never overwritten: {target}");
                        copied = false;
                        break;
                    }
                }
            }
            progress.SetIndeterminate(copied ? $"Published verified copy: {target}" : $"Already archived (verified): {target}");
            index.Record(identity, target, source, settings.Sources, content.Stamp,
                (occupiedContent ?? duplicateHandle ?? verification).Stamp, embeddedCreated, sourceCreated, new DateTimeOffset(stamp));
            index.Save(indexPath);
            return new(copied ? ArchiveOutcome.CopiedAndVerified : ArchiveOutcome.AlreadyArchived, source);

            void RecheckSource()
            {
                if (content.Length != size || File.GetLastWriteTimeUtc(source) != stamp)
                    throw new IOException("Source metadata changed during the protected read; retry required.");
            }
            void ByteProgress(string verb, string path, long done, long total) => progress.Report((int)(done * 100 / Math.Max(1, total)), 100,
                $"{verb} {path}: {done:N0}/{total:N0} bytes");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            bool unavailable = !Directory.Exists(settings.Destination) || (ex.HResult & 0xFFFF) is 0x70 or 0x27
                || temporary is not null && ex is UnauthorizedAccessException;
            return new(ArchiveOutcome.Failed, source, ex.Message, unavailable);
        }
        finally
        {
            occupiedContent?.Dispose();
            if (temporary is not null)
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                    index.OwnedPartials.Remove(temporary);
                    index.Save(indexPath);
                }
                catch { /* Retain ownership for recovery; completed copies stay in place. */ }
            }
        }
    }

    internal bool IsUnchangedVerifiedSource(string source, CancellationToken ct) =>
        !settings.VerifyExistingContent && index.IsUnchangedVerifiedSource(source, limiter, ct);

    private ArchiveContent? VerifiedLocation(ClipIdentity identity, CancellationToken ct, out string? verifiedPath, IOperationProgress? progress = null)
    {
        verifiedPath = null;
        if (index.FindEntry(identity) is { } entry)
            foreach (string location in entry.Locations.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!ArchivePaths.Contains(settings.Destination, location)) throw new IOException("Archive location outside destination.");
                    var keeper = new ArchiveContent(location, limiter, ct);
                    try
                    {
                        if (keeper.Identity(ct, (done, total) => progress?.Report((int)(done * 100 / Math.Max(1, total)), 100,
                            $"Verifying existing copy {location}: {done:N0}/{total:N0} bytes")) == identity)
                        { verifiedPath = location; return keeper; }
                    }
                    catch { keeper.Dispose(); throw; }
                    keeper.Dispose();
                }
                catch (OperationCanceledException) { throw; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                index.InvalidateLocation(location);
            }
        return null;
    }

    internal static ArchiveResult Run(string source, string destination, ISet<string> extensions,
        IOperationProgress progress, CancellationToken ct) => Run(new ArchiveSettings
        { Sources = [source], Destination = destination, Speed = ArchiveSpeed.Fast },
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipsManager"), extensions, progress, ct);

    internal static ArchiveResult Run(ArchiveSettings settings, string dataFolder, ISet<string> extensions,
        IOperationProgress progress, CancellationToken ct, IArchiveClock? clock = null)
    {
        int copied = 0, existing = 0, pending = 0, unchanged = 0;
        var errors = new List<string>();
        clock ??= new ArchiveClock();
        try
        {
            settings = settings.Clone();
            settings.Validate();
            var limiter = new IoRateLimiter(settings.Speed == ArchiveSpeed.Slow && settings.MegabytesPerSecond is { } cap
                ? (long)cap * 1_000_000 : null, clock);
            var archiver = new ClipArchiver(settings, dataFolder, limiter);
            void Yield() { if (settings.Speed == ArchiveSpeed.Slow) clock.Delay(TimeSpan.FromMilliseconds(1), ct); }
            progress.SetIndeterminate("Checking archive index for new or changed clips...");
            archiver.Reconcile(extensions, errors, ct, Yield, progress);
            var schedule = new ArchiveSchedule(settings, clock);
            foreach (string path in ArchivePaths.Videos(settings.Sources, extensions, errors, ct, Yield, recursive: false))
            {
                if (archiver.IsUnchangedVerifiedSource(path, ct)) { unchanged++; continue; }
                var before = new FileInfo(path);
                long size = before.Length;
                DateTime time = before.LastWriteTimeUtc;
                clock.Delay(TimeSpan.FromSeconds(settings.SettlingSeconds), ct);
                before.Refresh();
                if (!before.Exists || size != before.Length || time != before.LastWriteTimeUtc) { pending++; continue; }
                schedule.BeforeClip(ct);
                var result = archiver.Process(path, progress, ct);
                if (result.Outcome == ArchiveOutcome.CopiedAndVerified) copied++;
                else if (result.Outcome == ArchiveOutcome.AlreadyArchived) existing++;
                else errors.Add($"{path}: {result.Message}");
            }
            return new(copied, existing, pending, false, errors, unchanged);
        }
        catch (OperationCanceledException) { return new(copied, existing, pending, true, errors, unchanged); }
        catch (Exception ex) { errors.Add(ex.Message); return new(copied, existing, pending, false, errors, unchanged); }
    }
}
