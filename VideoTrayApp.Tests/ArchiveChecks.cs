using System.Diagnostics;
using VideoTrayApp;

internal static class ArchiveChecks
{
    internal static void Run(string parent, Action<string, uint, byte> write)
    {
        string root = Directory.CreateDirectory(Path.Combine(parent, "archive-checks")).FullName;
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(root, "archive")).FullName;
        string data = Path.Combine(root, "settings");
        var defaults = new ArchiveSettings();
        Check(defaults.Speed == ArchiveSpeed.Slow && !defaults.WatchContinuously && defaults.IntervalSeconds == 120
            && defaults.MegabytesPerSecond == 20 && defaults.SettlingSeconds == 10 && defaults.ReconcileSeconds == 300,
            "Initial Archive defaults are Slow/run-once with separate settling, scheduling, I/O and reconciliation controls");
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".avi" };
        var settings = new ArchiveSettings { Sources = [source, source + Path.DirectorySeparatorChar, nested, second], Destination = destination, Speed = ArchiveSpeed.Fast, SettlingSeconds = 0 };
        Check(ArchivePaths.Contains(Path.GetPathRoot(source)!, destination), "Containment validation correctly handles a drive root with a trailing separator");
        write(Path.Combine(source, "same-name.avi"), 2, 1);
        write(Path.Combine(nested, "same-name.avi"), 120, 2);
        File.Copy(Path.Combine(source, "same-name.avi"), Path.Combine(second, "duplicate.avi"));
        string batch = Directory.CreateDirectory(Path.Combine(destination, "batch_0001")).FullName;
        File.Copy(Path.Combine(nested, "same-name.avi"), Path.Combine(batch, "0001.avi"));
        File.WriteAllText(Path.Combine(source, "notes.txt"), "preserve notes");
        var before = Snapshot(source).Concat(Snapshot(second)).ToArray();
        string existing = Path.Combine(batch, "0001.avi");
        byte[] existingBytes = File.ReadAllBytes(existing);
        var result = ClipArchiver.Run(settings, data, exts, new TestProgress(), CancellationToken.None);
        Check(result.Copied == 1 && result.AlreadyArchived == 2 && result.Errors.Count == 0,
            "Recursive overlapping sources are deduplicated; existing shuffled archives and cross-root identities are reused");
        Check(before.SequenceEqual(Snapshot(source).Concat(Snapshot(second))) && File.ReadAllBytes(existing).SequenceEqual(existingBytes),
            "Archive leaves every source path/byte and existing destination untouched");
        var files = Directory.GetFiles(destination, "*.avi", SearchOption.AllDirectories);
        Check(files.Length == 2 && files.Any(p => Path.GetFileNameWithoutExtension(p).Length == 64),
            "Same filenames with different content create distinct hash-named copies without renaming existing clips");
        int duplicateCopyStarts = 0;
        var again = ClipArchiver.Run(settings, data, exts, new TestProgress { OnMessage = message =>
        {
            if (message.StartsWith("Copying ")) duplicateCopyStarts++;
        } }, CancellationToken.None);
        Check(again.Copied == 0 && again.AlreadyArchived == 0 && again.Unchanged == 3 && duplicateCopyStarts == 0,
            "Routine reruns skip unchanged verified sources after checking source and archive metadata");
        var indexPath = ArchiveIndex.IndexPath(data, destination);
        var index = ArchiveIndex.Load(indexPath, destination);
        Check(index.Entries.Sum(e => e.Sources.Count) == 4, "Index retains original names, full paths and every overlapping source-root context");
        string shuffled = Path.Combine(batch, "renamed.avi");
        File.Move(files.Single(p => Path.GetDirectoryName(p) == destination), shuffled);
        again = ClipArchiver.Run(settings, data, exts, new TestProgress(), CancellationToken.None);
        Check(again.Copied == 0, "Recursive reconciliation rediscovers moved/renamed archive copies");
        File.Delete(shuffled);
        again = ClipArchiver.Run(settings, data, exts, new TestProgress(), CancellationToken.None);
        Check(again.Copied == 1, "Missing archive content is recopied from a surviving source");

        var nestedDestination = settings.Clone(); nestedDestination.Destination = nested;
        Check(ClipArchiver.Run(nestedDestination, data, exts, new TestProgress(), CancellationToken.None).Errors.Count > 0,
            "Sources/destination containing one another are rejected before content work");
        var nestedSource = settings.Clone(); nestedSource.Sources = [batch];
        Check(ClipArchiver.Run(nestedSource, data, exts, new TestProgress(), CancellationToken.None).Errors.Count > 0,
            "Source inside archive destination is rejected");

        string isolated = Directory.CreateDirectory(Path.Combine(root, "isolated")).FullName;
        string isolatedDest = Directory.CreateDirectory(Path.Combine(root, "isolated-dest")).FullName;
        settings = new() { Sources = [isolated], Destination = isolatedDest, Speed = ArchiveSpeed.Fast, SettlingSeconds = 0 };
        string first = Path.Combine(isolated, "first.avi"), next = Path.Combine(isolated, "second.avi");
        write(first, 2, 31); write(next, 2, 32);
        using var cancel = new CancellationTokenSource();
        result = ClipArchiver.Run(settings, data, exts, new TestProgress { OnMessage = m => { if (m == "Copying second.avi...") cancel.Cancel(); } }, cancel.Token);
        Check(result.Cancelled && result.Copied == 1 && File.Exists(first) && File.Exists(next)
            && Directory.GetFiles(isolatedDest, "*.avi").Length == 1 && Directory.GetFiles(isolatedDest, "*.partial").Length == 0,
            "Cancellation retains completed verified copies and removes only owned incomplete files");
        using (var transaction = new OperationTransaction())
        {
            result = ClipArchiver.Run(settings, data, exts, new TestProgress(), CancellationToken.None);
            transaction.Rollback();
        }
        Check(result.Copied == 1 && Directory.GetFiles(isolatedDest, "*.avi").Length == 2,
            "Archive completion is independent of whole-run preset rollback");
        string lockClip = Path.Combine(isolated, "locked.avi"); write(lockClip, 2, 33);
        using (var locked = new FileStream(lockClip, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            result = ClipArchiver.Run(settings, data, exts, new TestProgress(), CancellationToken.None);
            Check(result.Errors.Any(e => e.Contains("locked")) && File.Exists(lockClip), "Write-open sources cannot be read/copied as protected clips");
        }
        using var verifyCancel = new CancellationTokenSource();
        result = ClipArchiver.Run(settings, data, exts, new TestProgress { OnMessage = m => { if (m == "Verifying locked.avi...") verifyCancel.Cancel(); } }, verifyCancel.Token);
        Check(result.Cancelled && Directory.GetFiles(isolatedDest, "*.avi").Length == 2 && Directory.GetFiles(isolatedDest, "*.partial").Length == 0,
            "Cancellation during read-back verification creates no completed entry");
        Check(!ArchiveIndex.Load(ArchiveIndex.IndexPath(data, isolatedDest), isolatedDest).SourceReceipts.ContainsKey(lockClip),
            "Interrupted copy verification never saves a source receipt that could skip recovery on restart");
        result = ClipArchiver.Run(settings, data, exts, new TestProgress { OnMessage = m =>
        {
            if (m == "Verifying locked.avi...")
                File.WriteAllBytes(Directory.GetFiles(isolatedDest, "*.partial").Single(), [0, 1]);
        } }, CancellationToken.None);
        Check(result.Errors.Count > 0 && File.Exists(lockClip) && Directory.GetFiles(isolatedDest, "*.avi").Length == 2,
            "A corrupted temporary copy fails verification without affecting its source");
        string target = Path.Combine(isolatedDest, ClipIdentifier.ReadIdentity(lockClip, CancellationToken.None).Hash.ToLowerInvariant() + ".avi");
        write(target, 3, 99);
        byte[] collision = File.ReadAllBytes(target);
        result = ClipArchiver.Run(settings, data, exts, new TestProgress(), CancellationToken.None);
        Check(result.Errors.Any(e => e.Contains("conflict")) && File.ReadAllBytes(target).SequenceEqual(collision),
            "An occupied hash filename with different content is never overwritten");
        File.Delete(target);
        var slow = settings.Clone(); slow.SettlingSeconds = 10;
        var growingClock = new FakeArchiveClock { OnDelay = _ => write(lockClip, 3, 34) };
        result = ClipArchiver.Run(slow, data, exts, new TestProgress(), CancellationToken.None, growingClock);
        Check(result.Pending >= 1 && !File.Exists(target), "Settling detects changed content metadata and leaves growing clips pending");

        indexPath = ArchiveIndex.IndexPath(data, isolatedDest);
        index = ArchiveIndex.Load(indexPath, isolatedDest);
        string owned = Path.Combine(isolatedDest, ".clipsmanager-" + Guid.NewGuid().ToString("N") + ".partial");
        string foreign = Path.Combine(isolatedDest, "someone-else.partial");
        File.WriteAllText(owned, "incomplete"); File.WriteAllText(foreign, "preserve");
        index.OwnedPartials.Add(owned); index.Save(indexPath);
        index.RecoverPartials(indexPath);
        Check(!File.Exists(owned) && File.Exists(foreign), "Recovery removes only exact temporary paths recorded by this archiver");
        FileStream? blockedSave = null;
        string publishedTarget = Path.Combine(isolatedDest, ClipIdentifier.ReadIdentity(lockClip, CancellationToken.None).Hash.ToLowerInvariant() + ".avi");
        result = ClipArchiver.Run(settings, data, exts, new TestProgress { OnMessage = message =>
        {
            if (message == $"Published verified copy: {publishedTarget}" && blockedSave is null)
                blockedSave = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        } }, CancellationToken.None);
        blockedSave?.Dispose();
        Check(result.Errors.Count > 0 && Directory.GetFiles(isolatedDest, "*.avi").Length == 3,
            "Index save failure after publication retains finalized content");
        result = ClipArchiver.Run(settings, data, exts, new TestProgress(), CancellationToken.None);
        Check(result.Copied == 0 && result.Errors.Count == 0, "A failed index save recovers through recursive content reconciliation without duplicate copies");
        RunForcedTermination(root, write, exts, data);
        RunLinksAndExtensions(root, write, data);
        RunCachedCorruption(root, write, exts);
        RunPersistentCache(root, write, exts);
        RunRoutineCache(root, write, exts);
        RunTiming();
        RunWorker(root, write, exts);
        RunInterrupt(root, write, exts);
        RunAvailabilityAndOverflow(root, write, exts);
        MeasureIndex(data, isolatedDest);
        MeasureLibrary(root, write, exts);
    }

    private static void RunForcedTermination(string root, Action<string, uint, byte> write, ISet<string> exts, string data)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "crash-source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(root, "crash-dest")).FullName;
        write(Path.Combine(source, "clip.avi"), 2, 55);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ArchiveChecks).Assembly.Location);
        foreach (string arg in new[] { "--archive-crash", source, destination, data }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        if (!child.WaitForExit(15000)) { child.Kill(); throw new Exception("Crash fixture timed out."); }
        var settings = new ArchiveSettings { Sources = [source], Destination = destination, Speed = ArchiveSpeed.Fast, SettlingSeconds = 0 };
        var result = ClipArchiver.Run(settings, data, exts, new TestProgress(), CancellationToken.None);
        Check(result.Copied == 0 && result.AlreadyArchived == 1 && Directory.GetFiles(destination, "*.avi").Length == 1,
            "Forced termination between publication and index save recovers finalized content without loss or extra copies");
    }
    private static void RunTiming()
    {
        var clock = new FakeArchiveClock();
        var settings = new ArchiveSettings { Speed = ArchiveSpeed.Slow, IntervalSeconds = 120 };
        var schedule = new ArchiveSchedule(settings, clock);
        schedule.BeforeClip(CancellationToken.None); DateTimeOffset first = clock.UtcNow;
        schedule.BeforeClip(CancellationToken.None);
        Check(clock.UtcNow - first == TimeSpan.FromSeconds(120), "Slow scheduling enforces the interval between clip starts with an injected clock");
        clock.Advance(TimeSpan.FromMinutes(10)); schedule.AfterPause(); first = clock.UtcNow;
        schedule.BeforeClip(CancellationToken.None);
        Check(clock.UtcNow - first == TimeSpan.FromSeconds(120), "Resume resets the schedule and cannot generate catch-up bursts");
        var limiter = new IoRateLimiter(20_000_000, clock); first = clock.UtcNow;
        for (int i = 0; i < 3; i++) limiter.Charge(20_000_000, CancellationToken.None);
        Check(clock.UtcNow - first == TimeSpan.FromSeconds(3) && limiter.BytesCharged == 60_000_000,
            "Aggregate content read/write budgeting uses decimal MB/s across passes");
        using var ct = new CancellationTokenSource(); ct.Cancel();
        try { new ArchiveClock().Delay(TimeSpan.FromMinutes(2), ct.Token); throw new Exception("Delay ignored cancellation."); }
        catch (OperationCanceledException) { Check(true, "Long scheduling waits cancel promptly"); }
    }
    private static void RunCachedCorruption(string root, Action<string, uint, byte> write, ISet<string> extensions)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "cached-source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(root, "cached-dest")).FullName;
        string clip = Path.Combine(source, "clip.avi"); write(clip, 2, 61);
        string stored = Path.Combine(destination, "stored.avi"); File.Copy(clip, stored);
        var settings = new ArchiveSettings { Sources = [source], Destination = destination, Speed = ArchiveSpeed.Fast, SettlingSeconds = 0 };
        var limiter = new IoRateLimiter(null, new ArchiveClock());
        var archiver = new ClipArchiver(settings, Path.Combine(root, "cached-settings"), limiter);
        archiver.Reconcile(extensions, [], CancellationToken.None);
        DateTime timestamp = File.GetLastWriteTimeUtc(stored); write(stored, 2, 62); File.SetLastWriteTimeUtc(stored, timestamp);
        archiver.Reconcile(extensions, [], CancellationToken.None);
        var result = archiver.Process(clip, new TestProgress(), CancellationToken.None);
        Check(result.Outcome == ArchiveOutcome.CopiedAndVerified && Directory.GetFiles(destination, "*.avi").Length == 2,
            "Same-size/time cached archive locations are only hints; actual verification detects corruption and protects the source with a fresh copy");
        archiver.Reconcile(extensions, [], CancellationToken.None);
        result = archiver.Process(clip, new TestProgress(), CancellationToken.None);
        Check(result.Outcome == ArchiveOutcome.AlreadyArchived, "Invalidated location hints are rebuilt by content and reruns reuse the intact verified copy");
    }
    private static void RunPersistentCache(string root, Action<string, uint, byte> write, ISet<string> extensions)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "persistent-source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(root, "persistent-dest")).FullName;
        string data = Path.Combine(root, "persistent-settings");
        string clip = Path.Combine(source, "clip.avi"); write(clip, 2, 63);
        var settings = new ArchiveSettings { Sources = [source], Destination = destination, Speed = ArchiveSpeed.Fast,
            WatchContinuously = true, SettlingSeconds = 0, ReconcileSeconds = 1 };
        var limiter = new IoRateLimiter(null, new ArchiveClock());
        var archiver = new ClipArchiver(settings, data, limiter);
        archiver.Reconcile(extensions, [], CancellationToken.None);
        Check(archiver.Process(clip, new TestProgress(), CancellationToken.None).Outcome == ArchiveOutcome.CopiedAndVerified,
            "Persistent cache starts only after a successful protected copy and full read-back verification");
        string stored = Directory.GetFiles(destination, "*.avi").Single();
        limiter = new IoRateLimiter(null, new ArchiveClock());
        archiver = new ClipArchiver(settings, data, limiter);
        archiver.Reconcile(extensions, [], CancellationToken.None);
        Check(archiver.IsUnchangedVerifiedSource(clip, CancellationToken.None) && limiter.BytesCharged == 0,
            "Restart loads saved archive candidates and verified source receipts without rereading unchanged clip content");

        using (var coordinator = new OperationCoordinator())
        using (var worker = new ArchiveWorker(coordinator, extensions, data))
        {
            worker.Start(settings);
            Wait(() => worker.Status.Current == "Watching for new or changed clips...", 10000);
            worker.RequestReconciliation();
            Thread.Sleep(1400);
            Check(worker.Status.Copied == 0 && worker.Status.AlreadyArchived == 0 && worker.Status.Queued == 0,
                "Watch restart and periodic reconciliation do not requeue previously verified unchanged sources");
            write(Path.Combine(source, "new.avi"), 3, 64);
            Wait(() => worker.Status.Copied == 1, 10000);
            Check(worker.Status.AlreadyArchived == 0, "A new arrival is copied without reverifying the historical source queue");
            worker.StopAsync().GetAwaiter().GetResult();
        }

        DateTime sourceTime = File.GetLastWriteTimeUtc(clip);
        string replacement = clip + ".replacement"; File.Copy(clip, replacement);
        File.SetLastWriteTimeUtc(replacement, sourceTime);
        File.Move(clip, clip + ".original"); File.Move(replacement, clip);
        Check(!archiver.IsUnchangedVerifiedSource(clip, CancellationToken.None),
            "A replaced source with identical bytes, length and write time invalidates its saved receipt");
        File.Delete(clip); File.Move(clip + ".original", clip);
        archiver.Process(clip, new TestProgress(), CancellationToken.None);
        write(clip, 2, 65); File.SetLastWriteTimeUtc(clip, sourceTime);
        Check(!archiver.IsUnchangedVerifiedSource(clip, CancellationToken.None),
            "Source content changes with a restored write time still invalidate the receipt through the file change stamp");
        write(clip, 2, 63); archiver.Process(clip, new TestProgress(), CancellationToken.None);
        DateTime archiveTime = File.GetLastWriteTimeUtc(stored);
        write(stored, 2, 66); File.SetLastWriteTimeUtc(stored, archiveTime);
        limiter = new IoRateLimiter(null, new ArchiveClock());
        archiver = new ClipArchiver(settings, data, limiter);
        archiver.Reconcile(extensions, [], CancellationToken.None);
        Check(limiter.BytesCharged > 0 && !archiver.IsUnchangedVerifiedSource(clip, CancellationToken.None),
            "Restart rehashes changed archive content even when its length and write time were preserved");
        Check(archiver.Process(clip, new TestProgress(), CancellationToken.None).Outcome == ArchiveOutcome.Failed,
            "A changed occupied hash filename is never overwritten by the cache optimization");
        File.Delete(stored);
        archiver.Reconcile(extensions, [], CancellationToken.None);
        Check(!archiver.IsUnchangedVerifiedSource(clip, CancellationToken.None)
            && archiver.Process(clip, new TestProgress(), CancellationToken.None).Outcome == ArchiveOutcome.CopiedAndVerified,
            "Missing archived content invalidates saved protection and is copied again from the source");
        string shuffled = Path.Combine(destination, "renamed.avi"); File.Move(stored, shuffled);
        archiver.Reconcile(extensions, [], CancellationToken.None);
        Check(archiver.IsUnchangedVerifiedSource(clip, CancellationToken.None)
            && Directory.GetFiles(destination, "*.avi").Length == 2,
            "Reconciliation reconnects a source receipt to a verified moved archive location without duplicating it");
        limiter = new IoRateLimiter(null, new ArchiveClock());
        archiver = new ClipArchiver(settings, data, limiter);
        archiver.Reconcile(extensions, [], CancellationToken.None);
        Check(archiver.Process(clip, new TestProgress(), CancellationToken.None).Outcome == ArchiveOutcome.AlreadyArchived
            && limiter.BytesCharged >= new FileInfo(clip).Length * 2,
            "Explicit processing still reads and verifies both source and archive content instead of trusting receipts");

        string indexPath = ArchiveIndex.IndexPath(data, destination);
        var cached = ArchiveIndex.Load(indexPath, destination);
        int hintCount = cached.LocationHints.Count;
        string uncached = Path.Combine(destination, "uncached.avi"); write(uncached, 4, 67);
        using var cancelled = new CancellationTokenSource();
        bool interrupted = false;
        try
        {
            cached.Refresh(extensions, new IoRateLimiter(null, new ArchiveClock()), [], cancelled.Token,
                progress: new TestProgress { OnMessage = message => { if (message.StartsWith("Indexing ")) cancelled.Cancel(); } });
        }
        catch (OperationCanceledException) { interrupted = true; }
        Check(interrupted && cached.LocationHints.Count == hintCount && !cached.LocationHints.ContainsKey(uncached),
            "A cancelled cache refresh preserves its previous complete location snapshot");
        File.Delete(uncached);
        cached.SourceReceipts[clip] = cached.SourceReceipts[clip] with { Stamp = cached.SourceReceipts[clip].Stamp with { Changed = 0 } };
        cached.Save(indexPath);
        archiver = new ClipArchiver(settings, data, new IoRateLimiter(null, new ArchiveClock()));
        Check(!archiver.IsUnchangedVerifiedSource(clip, CancellationToken.None),
            "Incomplete or unsupported saved change stamps fall back to verification rather than skipping a source");
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(indexPath))!.AsObject();
        legacy.Remove("LocationHints"); legacy.Remove("SourceReceipts"); File.WriteAllText(indexPath, legacy.ToJsonString());
        limiter = new IoRateLimiter(null, new ArchiveClock()); archiver = new ClipArchiver(settings, data, limiter);
        archiver.Reconcile(extensions, [], CancellationToken.None);
        Check(limiter.BytesCharged > 0 && !archiver.IsUnchangedVerifiedSource(clip, CancellationToken.None),
            "Indexes without cache fields receive a one-time content scan and never infer source receipts from provenance");
    }

    private static void RunRoutineCache(string root, Action<string, uint, byte> write, ISet<string> extensions)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "routine-source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(root, "routine-dest")).FullName;
        string data = Path.Combine(root, "routine-settings");
        for (int i = 0; i < 32; i++) write(Path.Combine(source, $"clip-{i}.avi"), (uint)i + 1, (byte)i);
        var settings = new ArchiveSettings { Sources = [source], Destination = destination,
            Speed = ArchiveSpeed.Fast, SettlingSeconds = 0 };
        var result = ClipArchiver.Run(settings, data, extensions, new TestProgress(), CancellationToken.None);
        Check(result.Copied == 32 && result.Errors.Count == 0, "Routine cache fixture fully verifies every initial copy");
        settings.Speed = ArchiveSpeed.Slow; settings.SettlingSeconds = 10; settings.IntervalSeconds = 120;
        var clock = new FakeArchiveClock();
        DateTimeOffset started = clock.UtcNow;
        string indexPath = ArchiveIndex.IndexPath(data, destination);
        byte[] saved = File.ReadAllBytes(indexPath);
        int contentMessages = 0;
        var progress = new TestProgress { OnMessage = message =>
        {
            if (message.StartsWith("Reading source ") || message.StartsWith("Indexing ")
                || message.StartsWith("Verifying existing copy ") || message.StartsWith("Copying ")) contentMessages++;
        } };
        using (var lockedIndex = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = ClipArchiver.Run(settings, data, extensions, progress, CancellationToken.None, clock);
        Check(result.Unchanged == 32 && result.Copied == 0 && result.AlreadyArchived == 0 && result.Errors.Count == 0
            && contentMessages == 0 && clock.UtcNow - started < TimeSpan.FromSeconds(1)
            && File.ReadAllBytes(indexPath).SequenceEqual(saved),
            "Quiet run-once restart skips all content I/O, settling and scheduling and does not rewrite the saved index");

        using (var coordinator = new OperationCoordinator())
        using (var worker = new ArchiveWorker(coordinator, extensions, data))
        using (var lockedIndex = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            worker.Start(settings); Wait(() => !worker.Running, 10000);
            Check(worker.Status.Unchanged == 32 && worker.Status.Copied == 0 && worker.Status.AlreadyArchived == 0
                && worker.Status.Failed == 0 && worker.Status.Warning.Length == 0,
                "Desktop Run once skips cached history instead of processing thousands of already-archived jobs");
        }

        string clip = Path.Combine(source, "clip-0.avi");
        var index = ArchiveIndex.Load(indexPath, destination);
        string stored = index.FindEntry(index.SourceReceipts[clip].Identity)!.Locations.Single();
        File.Delete(stored);
        result = ClipArchiver.Run(settings, data, extensions, new TestProgress(), CancellationToken.None, new FakeArchiveClock());
        Check(result.Unchanged == 31 && result.Copied == 1 && result.Errors.Count == 0 && File.Exists(stored),
            "Routine cached run recreates a missing archived copy while skipping intact history");
        DateTime time = File.GetLastWriteTimeUtc(clip);
        write(clip, 1, 100); File.SetLastWriteTimeUtc(clip, time);
        result = ClipArchiver.Run(settings, data, extensions, new TestProgress(), CancellationToken.None, new FakeArchiveClock());
        Check(result.Unchanged == 31 && result.Copied == 1 && result.Errors.Count == 0,
            "Routine cached run processes a changed source even when size and write time match the receipt");

        settings.VerifyExistingContent = true; settings.Save(data);
        settings = ArchiveSettings.Load(data);
        contentMessages = 0;
        result = ClipArchiver.Run(settings, data, extensions, progress, CancellationToken.None, new FakeArchiveClock());
        Check(settings.VerifyExistingContent && !settings.WatchContinuously && result.Unchanged == 0
            && result.AlreadyArchived == 32 && result.Errors.Count == 0 && contentMessages > 0,
            "Explicit full recheck survives settings loading and reads archive and source content instead of skipping receipts");
        settings.WatchContinuously = true;
        bool rejected = false;
        try { settings.Validate(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Full integrity rechecks cannot be configured to auto-resume as continuous watching");

        settings.WatchContinuously = false; settings.VerifyExistingContent = false;
        var archiver = new ClipArchiver(settings, data, new IoRateLimiter(null, new ArchiveClock()));
        string renamed = Path.Combine(destination, "renamed.avi"); File.Move(stored, renamed);
        bool saveFailed = false;
        using (var lockedIndex = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { archiver.Reconcile(extensions, [], CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { saveFailed = true; }
        }
        archiver.Reconcile(extensions, [], CancellationToken.None);
        Check(saveFailed && ArchiveIndex.Load(indexPath, destination).LocationHints.ContainsKey(renamed),
            "A failed reconciliation save is retried even when the next metadata scan is unchanged");
    }

    private static void RunLinksAndExtensions(string root, Action<string, uint, byte> write, string data)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "extension-source")).FullName;
        string dest = Directory.CreateDirectory(Path.Combine(root, "extension-dest")).FullName;
        string path = Path.Combine(source, "clip.avi"); write(path, 2, 72);
        // RIFF containers can be parsed under either extension. The test extension set explicitly includes both.
        File.Copy(path, Path.Combine(dest, "same-content.wav"));
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".avi", ".wav" };
        var settings = new ArchiveSettings { Sources = [source], Destination = dest, Speed = ArchiveSpeed.Fast, SettlingSeconds = 0 };
        var result = ClipArchiver.Run(settings, data, extensions, new TestProgress(), CancellationToken.None);
        Check(result.Copied == 0 && result.AlreadyArchived == 1 && result.Errors.Count == 0,
            "Identical content under another recognized extension reuses the existing archive copy");
        string link = Path.Combine(root, "linked-root");
        var command = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        command.ArgumentList.Add("/c"); command.ArgumentList.Add("mklink"); command.ArgumentList.Add("/J"); command.ArgumentList.Add(link); command.ArgumentList.Add(source);
        using (var child = Process.Start(command)!) { child.WaitForExit(); if (child.ExitCode != 0) throw new Exception("Junction fixture failed."); }
        try
        {
            settings.Sources = [link];
            result = ClipArchiver.Run(settings, data, extensions, new TestProgress(), CancellationToken.None);
            Check(result.Errors.Count > 0 && result.Copied == 0, "Linked roots and ancestors are rejected before any archiving");
            settings.Sources = [Path.Combine(link, "child")];
            Directory.CreateDirectory(Path.Combine(source, "child"));
            result = ClipArchiver.Run(settings, data, extensions, new TestProgress(), CancellationToken.None);
            Check(result.Errors.Count > 0, "A regular source below a linked ancestor is also rejected");
        }
        finally { Directory.Delete(link); }
    }
    private static void RunInterrupt(string root, Action<string, uint, byte> write, ISet<string> extensions)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "interrupt-source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(root, "interrupt-dest")).FullName;
        string clip = Path.Combine(source, "large.avi"); write(clip, 2, 89);
        using (var stream = new FileStream(clip, FileMode.Open, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            stream.Position = stream.Length;
            writer.Write(System.Text.Encoding.ASCII.GetBytes("JUNK")); writer.Write(2 * 1024 * 1024);
            writer.Write(new byte[2 * 1024 * 1024]);
            stream.Position = 4; writer.Write((uint)(stream.Length - 8));
        }
        using var coordinator = new OperationCoordinator();
        using var worker = new ArchiveWorker(coordinator, extensions, Path.Combine(root, "interrupt-settings"));
        worker.Start(new ArchiveSettings { Sources = [source], Destination = destination, Speed = ArchiveSpeed.Slow,
            IntervalSeconds = 0, MegabytesPerSecond = 1, SettlingSeconds = 0 });
        Wait(() => worker.Status.Current.StartsWith("Copying large.avi:") && worker.Status.Percent > 0, 15000);
        var timer = Stopwatch.StartNew();
        using (var foreground = coordinator.ForegroundAsync().GetAwaiter().GetResult())
        {
            Check(timer.Elapsed < TimeSpan.FromSeconds(2) && Directory.GetFiles(destination, "*.avi").Length == 0
                && Directory.GetFiles(destination, "*.partial").Length == 0 && File.Exists(clip),
                "Foreground priority interrupts an incomplete throttled copy at a chunk boundary and removes only its partial");
        }
        Wait(() => worker.Status.Copied == 1 && !worker.Running, 20000);
        Check(File.Exists(clip), "Interrupted work is requeued and later completes as a verified copy");
    }
    private static void RunAvailabilityAndOverflow(string root, Action<string, uint, byte> write, ISet<string> extensions)
    {
        string source = Path.Combine(root, "returning-source");
        string dest = Directory.CreateDirectory(Path.Combine(root, "returning-dest")).FullName;
        using var coordinator = new OperationCoordinator();
        using var worker = new ArchiveWorker(coordinator, extensions, Path.Combine(root, "returning-settings"));
        worker.Start(new ArchiveSettings { Sources = [source], Destination = dest, Speed = ArchiveSpeed.Fast,
            WatchContinuously = true, SettlingSeconds = 0, ReconcileSeconds = 1 });
        Wait(() => worker.Status.Warning.Contains("Source scan incomplete"), 10000);
        Directory.CreateDirectory(source); write(Path.Combine(source, "returned.avi"), 2, 91);
        worker.RequestReconciliation();
        Wait(() => worker.Status.Copied == 1, 10000);
        Check(true, "Unavailable source roots remain configured and register recursive watchers when they return");
        string offline = dest + "-offline"; Directory.Move(dest, offline);
        write(Path.Combine(source, "queued.avi"), 2, 92);
        worker.Enqueue(Path.Combine(source, "queued.avi"), true);
        Wait(() => worker.Status.Warning.Contains("Archive suspended"), 10000);
        Check(worker.Status.Queued > 0 && worker.Running, "An unavailable destination suspends copying without discarding queued sources");
        Directory.Move(offline, dest); worker.RequestReconciliation();
        Wait(() => worker.Status.Copied == 2, 15000);
        worker.Pause();
        for (int i = 0; i < ArchiveWorker.QueueCapacity + 100; i++) worker.Enqueue(Path.Combine(source, "event-" + i + ".avi"), true);
        for (int i = 0; i < 100; i++) worker.Enqueue(Path.Combine(source, "event-0.avi"), true);
        Check(worker.Status.Queued == ArchiveWorker.QueueCapacity,
            "Event storms remain bounded at 4,096 paths and repeated events collapse into one pending path");
        string missed = Path.Combine(source, "overflow-arrival.avi"); write(missed, 2, 93); worker.Enqueue(missed, true);
        worker.Resume();
        Wait(() => worker.Status.Copied == 3, 15000);
        Check(File.Exists(missed), "Reconciliation recovers arrivals dropped when the event queue overflows");
        worker.StopAsync().GetAwaiter().GetResult();
        // Disk-full classification is injected without exhausting a real disk.
        var settings = new ArchiveSettings { Sources = [source], Destination = dest, Speed = ArchiveSpeed.Fast };
        string full = Path.Combine(source, "full.avi"); write(full, 2, 94);
        var archiver = new ClipArchiver(settings, Path.Combine(root, "full-settings"), new IoRateLimiter(null, new ArchiveClock()));
        var result = archiver.Process(full, new TestProgress { OnMessage = m =>
        {
            if (m == "Copying full.avi...") throw new IOException("Injected disk full", unchecked((int)0x80070070));
        } }, CancellationToken.None);
        Check(result.DestinationUnavailable && File.Exists(full) && Directory.GetFiles(dest, "*.partial").Length == 0,
            "Disk-full errors signal destination suspension and leave source content and completed archive copies intact");
    }
    private static void RunWorker(string root, Action<string, uint, byte> write, ISet<string> exts)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "watch-source")).FullName;
        string dest = Directory.CreateDirectory(Path.Combine(root, "watch-dest")).FullName;
        using var coordinator = new OperationCoordinator();
        using var worker = new ArchiveWorker(coordinator, exts, Path.Combine(root, "worker-settings"));
        var settings = new ArchiveSettings { Sources = [source], Destination = dest, WatchContinuously = true,
            Speed = ArchiveSpeed.Fast, SettlingSeconds = 1, ReconcileSeconds = 2 };
        worker.Start(settings);
        string nested = Directory.CreateDirectory(Path.Combine(source, "new-subfolder")).FullName;
        string clip = Path.Combine(nested, "growing.avi");
        write(clip, 2, 81);
        using (var growing = new FileStream(clip, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            Thread.Sleep(1300);
            Check(Directory.GetFiles(dest, "*.avi").Length == 0, "Watcher leaves write-open growing clips pending");
        }
        Wait(() => Directory.GetFiles(dest, "*.avi").Length == 1 && worker.Status.LastSuccess is not null, 15000);
        Check(worker.Status.LastSuccess is not null && File.Exists(clip), "Recursive watcher protects new subfolder clips after settling without changing sources");
        worker.Pause();
        string next = Path.Combine(source, "paused.avi"); write(next, 2, 82);
        Thread.Sleep(1300);
        Check(Directory.GetFiles(dest, "*.avi").Length == 1, "Pause suspends content work while watcher subscriptions retain arrivals");
        worker.Resume();
        Wait(() => Directory.GetFiles(dest, "*.avi").Length == 2, 15000);
        using (var foreground = coordinator.ForegroundAsync().GetAwaiter().GetResult())
        {
            write(Path.Combine(source, "foreground.avi"), 2, 83);
            Thread.Sleep(1300);
            Check(Directory.GetFiles(dest, "*.avi").Length == 2, "Foreground operations suspend background I/O while the watcher stays subscribed");
        }
        worker.RequestReconciliation();
        Wait(() => Directory.GetFiles(dest, "*.avi").Length == 3, 15000);
        string missing = Directory.GetFiles(dest, "*.avi").First(); File.Delete(missing);
        worker.RequestReconciliation();
        Wait(() => Directory.GetFiles(dest, "*.avi").Length == 3, 15000);
        Check(true, "Periodic/requeued reconciliation resumes copying and recreates a vanished archive copy");
        worker.StopAsync().GetAwaiter().GetResult();
        Check(!worker.Running && Directory.GetFiles(dest, "*.partial").Length == 0, "Stop shuts down watchers safely and retains verified clips");
        settings.Save(Path.Combine(root, "worker-settings"));
        Check(ArchiveSettings.Load(Path.Combine(root, "worker-settings")).WatchContinuously,
            "Continuous watching choice and separate archive configuration survive restart");
        settings.WatchContinuously = false;
        worker.Start(settings); Wait(() => !worker.Running, 15000);
        Check(worker.Status.AlreadyArchived == 0 && worker.Status.Unchanged == 3,
            "Run once finishes after checking cached source and archive stamps without reverifying existing content");
        settings.VerifyExistingContent = true;
        worker.Start(settings); Wait(() => !worker.Running, 15000);
        Check(worker.Status.AlreadyArchived == 3 && worker.Status.Unchanged == 0,
            "Full recheck worker drains its queue and verifies every existing source and archive copy");
    }
    private static void MeasureIndex(string data, string destination)
    {
        var index = new ArchiveIndex { Destination = destination };
        for (int i = 0; i < 10000; i++) index.Entries.Add(new ArchiveEntry { Identity = new(i, TimeSpan.FromSeconds(i + 1), i.ToString("X64")),
            Locations = [Path.Combine(destination, i + ".avi")], Sources = [new("C:\\Source\\" + i + ".avi", i + ".avi", "C:\\Source", "Source")] });
        var timer = Stopwatch.StartNew(); string path = Path.Combine(data, "benchmark.json"); index.Save(path); timer.Stop();
        Console.WriteLine($"MEASURE: 10,000-entry JSON index: {new FileInfo(path).Length:N0} bytes, atomic save {timer.ElapsedMilliseconds} ms.");
    }
    private static void MeasureLibrary(string root, Action<string, uint, byte> write, ISet<string> extensions)
    {
        string destination = Directory.CreateDirectory(Path.Combine(root, "library-index")).FullName;
        for (int i = 0; i < 1000; i++) write(Path.Combine(destination, i + ".avi"), (uint)i + 1, (byte)(i % 255));
        var limiter = new IoRateLimiter(null, new ArchiveClock());
        var index = new ArchiveIndex { Destination = destination };
        var timer = Stopwatch.StartNew(); index.Refresh(extensions, limiter, [], CancellationToken.None); timer.Stop();
        long fullScan = timer.ElapsedMilliseconds;
        timer.Restart(); index.Refresh(extensions, limiter, [], CancellationToken.None); timer.Stop();
        Check(index.Entries.Count == 1000, "Initial library indexing reads every unique identity and later reconciliation retains candidate locations");
        string indexPath = ArchiveIndex.IndexPath(Path.Combine(root, "library-settings"), destination);
        index.Save(indexPath);
        var restartedLimiter = new IoRateLimiter(null, new ArchiveClock());
        var restarted = ArchiveIndex.Load(indexPath, destination);
        restarted.Refresh(extensions, restartedLimiter, [], CancellationToken.None);
        Check(restarted.Entries.Count == 1000 && restartedLimiter.BytesCharged == 0,
            "A 1,000-clip library restart performs metadata reconciliation with zero clip-content I/O");
        Console.WriteLine($"MEASURE: 1,000 generated clips: initial content indexing {fullScan} ms; metadata reconciliation {timer.ElapsedMilliseconds} ms.");
        string source = Directory.CreateDirectory(Path.Combine(root, "throughput-source")).FullName;
        string output = Directory.CreateDirectory(Path.Combine(root, "throughput-dest")).FullName;
        string clip = Path.Combine(source, "throughput.avi"); write(clip, 2, 102);
        using (var stream = new FileStream(clip, FileMode.Open, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            stream.Position = stream.Length; writer.Write(System.Text.Encoding.ASCII.GetBytes("JUNK")); writer.Write(2 * 1024 * 1024);
            writer.Write(new byte[2 * 1024 * 1024]); stream.Position = 4; writer.Write((uint)(stream.Length - 8));
        }
        var settings = new ArchiveSettings { Sources = [source], Destination = output };
        limiter = new IoRateLimiter(20_000_000, new ArchiveClock());
        var archiver = new ClipArchiver(settings, Path.Combine(root, "throughput-settings"), limiter);
        timer.Restart(); var result = archiver.Process(clip, new TestProgress(), CancellationToken.None); timer.Stop();
        Check(result.Outcome == ArchiveOutcome.CopiedAndVerified && limiter.BytesCharged >= new FileInfo(clip).Length * 3
            && timer.Elapsed.TotalSeconds >= (double)limiter.BytesCharged / 20_000_000 * 0.95,
            "Real Slow content I/O includes copy reads/writes, metadata and read-back verification under the 20 MB/s aggregate cap");
        Console.WriteLine($"MEASURE: throttled copy+verification {limiter.BytesCharged:N0} I/O bytes in {timer.Elapsed.TotalSeconds:F3}s, {(limiter.BytesCharged / timer.Elapsed.TotalSeconds / 1_000_000):F2} MB/s.");
    }
    internal static void Wait(Func<bool> predicate, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate()) { if (timer.ElapsedMilliseconds > milliseconds) throw new Exception("Timed out waiting for Archive worker."); Thread.Sleep(50); }
    }
    private static string[] Snapshot(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)
        .Select(p => p + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
}

internal sealed class FakeArchiveClock : IArchiveClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    public Action<TimeSpan>? OnDelay { get; init; }
    public void Delay(TimeSpan delay, CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (delay > TimeSpan.Zero) { UtcNow += delay; OnDelay?.Invoke(delay); } }
    internal void Advance(TimeSpan delay) => UtcNow += delay;
}
