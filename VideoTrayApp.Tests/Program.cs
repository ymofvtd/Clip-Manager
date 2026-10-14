using VideoTrayApp;

if (args.Length == 4 && args[0] == "--archive-crash")
{
    ClipArchiver.Run(new ArchiveSettings { Sources = [args[1]], Destination = args[2], Speed = ArchiveSpeed.Fast, SettlingSeconds = 0 },
        args[3], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4" }, new TestProgress
        { OnMessage = message => { if (message.StartsWith("Published verified copy:")) System.Diagnostics.Process.GetCurrentProcess().Kill(); } }, CancellationToken.None);
    return;
}

// Integration checks use generated MP4 metadata and isolated, disposable files.
string root = Path.Combine(Path.GetTempPath(), "ClipIdentifierTests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    Mp4Checks.Run(root, WriteMp4);
    if (args.Contains("--archive-only"))
    {
        ArchiveChecks.Run(root, WriteMp4);
        return;
    }
    UxChecks.Run(root, WriteMp4);
    if (args.Contains("--ux-only")) return;
    string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
    string nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
    string destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
    string reference = Path.Combine(source, "reference.mp4");
    WriteMp4(reference, 2, 11);
    string duplicate = Path.Combine(nested, "renamed.MP4");
    File.Copy(reference, duplicate);
    string differentContent = Path.Combine(source, "different-content.mp4");
    WriteMp4(differentContent, 2, 12);
    WriteMp4(Path.Combine(source, "different-duration.mp4"), 3, 11);
    File.WriteAllBytes(Path.Combine(source, "unreadable.mp4"), new byte[new FileInfo(reference).Length]);
    var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4" };
    var progress = new TestProgress();

    var found = ClipIdentifier.Find(reference, source, extensions, progress, CancellationToken.None);
    Check(found.Matches.SequenceEqual([duplicate]), "Recursive matching excludes reference and rejects same-size, same-duration content with a different hash");
    Check(found.Errors.Count == 1, "Unreadable clips are reported without aborting the search");

    string existing = Path.Combine(destination, "renamed.MP4");
    File.WriteAllText(existing, "keep this destination file");
    string moved = ClipIdentifier.Apply(found, reference, destination, progress, CancellationToken.None);
    Check(!File.Exists(duplicate) && File.Exists(Path.Combine(destination, "renamed__1.MP4")), "Move handles a filename collision");
    Check(File.ReadAllText(existing) == "keep this destination file" && File.Exists(reference), "Existing destination and reference are preserved");
    Check(moved.Contains("Moved: 1"), "Move summary counts completed actions");

    File.Copy(reference, duplicate);
    var changed = ClipIdentifier.Find(reference, source, extensions, progress, CancellationToken.None);
    WriteMp4(duplicate, 2, 99);
    string rejected = ClipIdentifier.Apply(changed, reference, destination, progress, CancellationToken.None);
    Check(File.Exists(duplicate) && rejected.Contains("Errors: 1"), "Files changed after search remain untouched");

    File.Copy(reference, duplicate, overwrite: true);
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    bool cancelled = false;
    try { ClipIdentifier.Find(reference, source, extensions, progress, cts.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "Search supports cancellation");
    string cancelledAction = ClipIdentifier.Apply(found, reference, destination, progress, cts.Token);
    Check(File.Exists(duplicate) && cancelledAction.Contains("Moved: 0"), "Cancelled actions preserve files and report no completed moves");

    var alreadyThere = new ClipSearchResult(found.Reference, [reference], []);
    string excluded = ClipIdentifier.Apply(alreadyThere, reference, null, progress, CancellationToken.None);
    Check(File.Exists(reference) && excluded.Contains("Sent to Recycle Bin: 0"), "Action layer also protects reference clip");
    var inDestination = new ClipSearchResult(found.Reference, [Path.Combine(destination, "renamed__1.MP4")], []);
    string skipped = ClipIdentifier.Apply(inDestination, reference, destination, progress, CancellationToken.None);
    Check(skipped.Contains("Skipped (already in destination): 1"), "Clips already in destination are left in place");

    // Only this generated duplicate is sent to the Recycle Bin.
    string deleted = ClipIdentifier.Apply(found, reference, null, progress, CancellationToken.None);
    Check(!File.Exists(duplicate) && File.Exists(reference) && deleted.Contains("Sent to Recycle Bin: 1"), "Delete recycles the match and preserves the reference");

    string first = Path.Combine(nested, "first.mp4");
    string second = Path.Combine(nested, "second.mp4");
    File.Copy(reference, first);
    File.Copy(reference, second);
    using var partialCts = new CancellationTokenSource();
    var partialProgress = new TestProgress { OnReport = current => { if (current == 1) partialCts.Cancel(); } };
    var twoMatches = new ClipSearchResult(found.Reference, [first, second], []);
    string partial = ClipIdentifier.Apply(twoMatches, reference, destination, partialProgress, partialCts.Token);
    Check(!File.Exists(first) && File.Exists(second) && partial.Contains("Moved: 1") && partial.Contains("Unprocessed: 1"),
        "Cancellation after a move reports partial completion and preserves remaining matches");
    string cleanupFolder = Directory.CreateDirectory(Path.Combine(root, "cleanup")).FullName;
    string cleanupNested = Directory.CreateDirectory(Path.Combine(cleanupFolder, "nested")).FullName;
    string keeper = Path.Combine(cleanupFolder, "a.mp4");
    string extra = Path.Combine(cleanupFolder, "z.MP4");
    string extraNested = Path.Combine(cleanupNested, "shuffled.mp4");
    WriteMp4(keeper, 2, 41);
    File.Copy(keeper, extra);
    File.Copy(keeper, extraNested);
    string otherKeeper = Path.Combine(cleanupFolder, "b.mp4");
    string otherExtra = Path.Combine(cleanupFolder, "y.mp4");
    WriteMp4(otherKeeper, 2, 42);
    File.Copy(otherKeeper, otherExtra);
    string durationOnly = Path.Combine(cleanupFolder, "different-duration.mp4");
    WriteMp4(durationOnly, 3, 41);
    File.WriteAllBytes(Path.Combine(cleanupFolder, "invalid.mp4"), new byte[new FileInfo(keeper).Length]);
    File.Copy(keeper, Path.Combine(cleanupFolder, "ignored.txt"));
    string uniqueSize = Path.Combine(cleanupFolder, "unique.mp4");
    WriteMp4(uniqueSize, 2, 41);
    using (var append = new FileStream(uniqueSize, FileMode.Append)) append.WriteByte(0);

    var duplicates = ClipDuplicateCleaner.Find(cleanupFolder, extensions, progress, CancellationToken.None);
    Check(duplicates.Groups.Count == 2 && duplicates.DuplicateCount == 3,
        "Folder scan finds multiple independent duplicate groups and all extra copies");
    var firstGroup = duplicates.Groups.Single(group => group.Keep == keeper);
    Check(firstGroup.Duplicates.SequenceEqual([extraNested, extra]),
        "Folder scan keeps the first path alphabetically and includes nested, renamed, mixed-case clips");
    Check(duplicates.Errors.Count == 1 && duplicates.Groups.All(group => !group.Duplicates.Contains(durationOnly)),
        "Invalid metadata is reported and different durations, unique sizes and non-video files are excluded");
    bool cleanupCancelled = false;
    try { ClipDuplicateCleaner.Find(cleanupFolder, extensions, progress, cts.Token); }
    catch (OperationCanceledException) { cleanupCancelled = true; }
    Check(cleanupCancelled, "Folder scan supports cancellation");
    string cancelledCleanup = ClipDuplicateCleaner.Apply(duplicates, progress, cts.Token);
    Check(File.Exists(extra) && File.Exists(extraNested) && cancelledCleanup.Contains("Sent to Recycle Bin: 0"),
        "Cancelled cleanup does not remove any copies");

    var oneGroup = new DuplicateClipResult([firstGroup], []);
    WriteMp4(keeper, 2, 77);
    string keeperChanged = ClipDuplicateCleaner.Apply(oneGroup, progress, CancellationToken.None);
    Check(File.Exists(extra) && File.Exists(extraNested) && keeperChanged.Contains("Left untouched: 2"),
        "A changed kept copy protects every duplicate in its group");
    File.Delete(keeper);
    string keeperMissing = ClipDuplicateCleaner.Apply(oneGroup, progress, CancellationToken.None);
    Check(File.Exists(extra) && File.Exists(extraNested) && keeperMissing.Contains("Sent to Recycle Bin: 0"),
        "A missing kept copy protects every duplicate in its group");
    File.Copy(extra, keeper);
    WriteMp4(extraNested, 2, 88);
    string duplicateChanged = ClipDuplicateCleaner.Apply(oneGroup, progress, CancellationToken.None);
    Check(File.Exists(keeper) && File.Exists(extraNested) && !File.Exists(extra)
        && duplicateChanged.Contains("Sent to Recycle Bin: 1") && duplicateChanged.Contains("Left untouched: 1"),
        "Cleanup rechecks duplicates, recycles unchanged extras and preserves changed clips and the kept copy");

    File.Copy(keeper, extraNested, overwrite: true);
    File.Copy(keeper, extra);
    using var cleanupPartialCts = new CancellationTokenSource();
    var cleanupPartialProgress = new TestProgress
    {
        OnReport = current => { if (current == 1) cleanupPartialCts.Cancel(); }
    };
    string cleanupPartial = ClipDuplicateCleaner.Apply(oneGroup, cleanupPartialProgress, cleanupPartialCts.Token);
    Check(File.Exists(keeper) && !File.Exists(extraNested) && File.Exists(extra)
        && cleanupPartial.Contains("Sent to Recycle Bin: 1") && cleanupPartial.Contains("Unprocessed: 1"),
        "Partial cleanup cancellation preserves the kept copy and reports remaining duplicates");

    var remaining = ClipDuplicateCleaner.Find(cleanupFolder, extensions, progress, CancellationToken.None);
    string allCleaned = ClipDuplicateCleaner.Apply(remaining, progress, CancellationToken.None);
    Check(File.Exists(keeper) && File.Exists(otherKeeper) && !File.Exists(extra) && !File.Exists(otherExtra)
        && File.Exists(durationOnly) && File.Exists(uniqueSize) && allCleaned.Contains("Sent to Recycle Bin: 2"),
        "Whole-folder cleanup leaves one copy per group and preserves distinct clips");
    Check(ClipDuplicateCleaner.Find(cleanupFolder, extensions, progress, CancellationToken.None).DuplicateCount == 0,
        "A second scan finds no extra copies after cleanup");
    string batchSource = Directory.CreateDirectory(Path.Combine(root, "batch-source")).FullName;
    string batchDestination = Directory.CreateDirectory(Path.Combine(batchSource, "batch")).FullName;
    string batchBackup = Directory.CreateDirectory(Path.Combine(batchSource, "Backup")).FullName;
    WriteMp4(Path.Combine(batchSource, "10.mp4"), 2, 51);
    File.Copy(Path.Combine(batchSource, "10.mp4"), Path.Combine(batchSource, "20.mp4"));
    File.Copy(Path.Combine(batchSource, "10.mp4"), Path.Combine(batchSource, "30.mp4"));
    WriteMp4(Path.Combine(batchSource, "40.mp4"), 2, 52);
    WriteMp4(Path.Combine(batchSource, "50.mp4"), 2, 53);
    File.Copy(Path.Combine(batchSource, "50.mp4"), Path.Combine(batchSource, "60.mp4"));
    File.Copy(Path.Combine(batchSource, "10.mp4"), Path.Combine(batchBackup, "10.mp4"));
    string unnumbered = Path.Combine(batchSource, "shuffled.mp4");
    File.Copy(Path.Combine(batchSource, "10.mp4"), unnumbered);
    int batchScans = 0;
    var batchProgress = new TestProgress
    {
        OnMessage = message =>
        {
            if (message == "Checking prepared batch for duplicates...") batchScans++;
            if (message.StartsWith("Backing up "))
            {
                Check(batchScans >= 2 && !File.Exists(Path.Combine(batchSource, "20.mp4"))
                    && !File.Exists(Path.Combine(batchSource, "30.mp4")),
                    "Backup starts only after cleanup, refill and a duplicate-free scan");
            }
        }
    };
    string batchResult = ClipBatchPreparer.Prepare(batchSource, batchDestination, TimeSpan.FromSeconds(6),
        extensions, batchProgress, CancellationToken.None, ".mp4", new PreserveOrderRandom());
    Check(batchResult.Contains("Moved and backed up: 3") && batchResult.Contains("Duplicates sent to Recycle Bin: 2")
        && Directory.GetFiles(batchDestination).Select(Path.GetFileName).Order().SequenceEqual(["1.mp4", "2.mp4", "3.mp4"]),
        "Batch refills duration freed by duplicates and moves only unique numbered clips");
    Check(File.Exists(Path.Combine(batchSource, "60.mp4")) && File.Exists(unnumbered)
        && File.Exists(Path.Combine(batchBackup, "10.mp4")) && File.Exists(Path.Combine(batchBackup, "10__1.mp4"))
        && !File.Exists(Path.Combine(batchBackup, "20.mp4")),
        "Unselected clips and existing backups are preserved; only final unique clips get new backups");

    string refillSource = Directory.CreateDirectory(Path.Combine(root, "refill-source")).FullName;
    string refillDestination = Directory.CreateDirectory(Path.Combine(root, "refill-destination")).FullName;
    WriteMp4(Path.Combine(refillSource, "10.mp4"), 2, 61);
    File.Copy(Path.Combine(refillSource, "10.mp4"), Path.Combine(refillDestination, "a.mp4"));
    File.Copy(Path.Combine(refillSource, "10.mp4"), Path.Combine(refillDestination, "z.mp4"));
    WriteMp4(Path.Combine(refillSource, "20.mp4"), 2, 62);
    File.Copy(Path.Combine(refillSource, "20.mp4"), Path.Combine(refillSource, "30.mp4"));
    WriteMp4(Path.Combine(refillSource, "40.mp4"), 2, 63);
    int refillScans = 0;
    var refillProgress = new TestProgress
    {
        OnMessage = message => { if (message == "Checking prepared batch for duplicates...") refillScans++; }
    };
    string refilled = ClipBatchPreparer.Prepare(refillSource, refillDestination, TimeSpan.FromSeconds(6),
        extensions, refillProgress, CancellationToken.None, ".mp4", new PreserveOrderRandom());
    Check(refillScans == 3 && refilled.Contains("Duplicates sent to Recycle Bin: 3")
        && File.Exists(Path.Combine(refillDestination, "a.mp4")) && !File.Exists(Path.Combine(refillDestination, "z.mp4"))
        && File.Exists(Path.Combine(refillDestination, "1.mp4")) && File.Exists(Path.Combine(refillDestination, "2.mp4")),
        "Destination copies take priority and duplicates introduced during refill trigger another cleanup cycle");

    string exhaustedSource = Directory.CreateDirectory(Path.Combine(root, "exhausted-source")).FullName;
    string exhaustedDestination = Directory.CreateDirectory(Path.Combine(root, "exhausted-destination")).FullName;
    WriteMp4(Path.Combine(exhaustedSource, "10.mp4"), 2, 71);
    File.Copy(Path.Combine(exhaustedSource, "10.mp4"), Path.Combine(exhaustedSource, "20.mp4"));
    string exhausted = ClipBatchPreparer.Prepare(exhaustedSource, exhaustedDestination, TimeSpan.FromSeconds(10),
        extensions, progress, CancellationToken.None, ".mp4", new PreserveOrderRandom());
    Check(exhausted.Contains("Moved and backed up: 1") && Directory.GetFiles(exhaustedDestination).Length == 1,
        "Exhausted source completes with a smaller duplicate-free batch");

    string failedSource = Directory.CreateDirectory(Path.Combine(root, "failed-source")).FullName;
    string failedDestination = Directory.CreateDirectory(Path.Combine(root, "failed-destination")).FullName;
    WriteMp4(Path.Combine(failedSource, "10.mp4"), 2, 81);
    File.Copy(Path.Combine(failedSource, "10.mp4"), Path.Combine(failedSource, "20.mp4"));
    bool changedBatch = false;
    var changedBatchProgress = new TestProgress
    {
        OnMessage = message =>
        {
            if (!changedBatch && message == "Verifying duplicate: 20.mp4")
            {
                changedBatch = true;
                WriteMp4(Path.Combine(failedSource, "20.mp4"), 2, 82);
            }
        }
    };
    bool cleanupFailed = false;
    try
    {
        ClipBatchPreparer.Prepare(failedSource, failedDestination, TimeSpan.FromSeconds(6),
            extensions, changedBatchProgress, CancellationToken.None, ".mp4", new PreserveOrderRandom());
    }
    catch (IOException) { cleanupFailed = true; }
    Check(cleanupFailed && !Directory.Exists(Path.Combine(failedSource, "Backup"))
        && Directory.GetFiles(failedDestination).Length == 0 && Directory.GetFiles(failedSource).Length == 2,
        "Cleanup verification failure stops before backup or moving without retrying indefinitely");

    File.Copy(Path.Combine(failedSource, "10.mp4"), Path.Combine(failedSource, "20.mp4"), overwrite: true);
    using var batchCts = new CancellationTokenSource();
    var cancelBatchProgress = new TestProgress
    {
        OnMessage = message => { if (message.StartsWith("Sent to Recycle Bin: 1")) batchCts.Cancel(); }
    };
    bool batchCancelled = false;
    try
    {
        ClipBatchPreparer.Prepare(failedSource, failedDestination, TimeSpan.FromSeconds(6),
            extensions, cancelBatchProgress, batchCts.Token, ".mp4", new PreserveOrderRandom());
    }
    catch (OperationCanceledException) { batchCancelled = true; }
    Check(batchCancelled && File.Exists(Path.Combine(failedSource, "10.mp4"))
        && !Directory.Exists(Path.Combine(failedSource, "Backup")) && Directory.GetFiles(failedDestination).Length == 0,
        "Cancellation after recycling stops batch preparation before backup and moving");
    bool invalidDestination = false;
    try
    {
        ClipBatchPreparer.Prepare(batchSource, batchBackup, TimeSpan.FromSeconds(6),
            extensions, progress, CancellationToken.None, ".mp4", new PreserveOrderRandom());
    }
    catch (IOException) { invalidDestination = true; }
    Check(invalidDestination, "Batch preparation rejects using the Backup folder as destination");
    string invalidSource = Directory.CreateDirectory(Path.Combine(root, "invalid-batch-source")).FullName;
    string invalidDest = Directory.CreateDirectory(Path.Combine(root, "invalid-batch-dest")).FullName;
    File.WriteAllBytes(Path.Combine(invalidSource, "10.mp4"), [0, 1, 2]);
    bool unreadableBatch = false;
    try
    {
        ClipBatchPreparer.Prepare(invalidSource, invalidDest, TimeSpan.FromSeconds(6),
            extensions, progress, CancellationToken.None, ".mp4", new PreserveOrderRandom());
    }
    catch (Exception ex) when (ex is not OperationCanceledException) { unreadableBatch = true; }
    Check(unreadableBatch && !Directory.Exists(Path.Combine(invalidSource, "Backup"))
        && Directory.GetFiles(invalidDest).Length == 0 && File.Exists(Path.Combine(invalidSource, "10.mp4")),
        "Unreadable clips stop preparation before backup and moving");

    string collisionSource = Directory.CreateDirectory(Path.Combine(root, "collision-source")).FullName;
    string collisionDest = Directory.CreateDirectory(Path.Combine(root, "collision-dest")).FullName;
    WriteMp4(Path.Combine(collisionSource, "10.mp4"), 2, 91);
    WriteMp4(Path.Combine(collisionDest, "10.mp4"), 2, 92);
    var collisionIdentity = ClipIdentifier.ReadIdentity(Path.Combine(collisionDest, "10.mp4"), CancellationToken.None);
    string collision = ClipBatchPreparer.Prepare(collisionSource, collisionDest, TimeSpan.FromSeconds(6),
        extensions, progress, CancellationToken.None, ".mp4", new PreserveOrderRandom());
    Check(collision.Contains("Moved and backed up: 1") && File.Exists(Path.Combine(collisionDest, "1.mp4"))
        && !File.Exists(Path.Combine(collisionSource, "10.mp4"))
        && ClipIdentifier.ReadIdentity(Path.Combine(collisionDest, "10.mp4"), CancellationToken.None) == collisionIdentity,
        "An original filename collision preserves the existing clip and moves the new clip with a numeric name");
    string orderedSource = Directory.CreateDirectory(Path.Combine(root, "ordered-source")).FullName;
    string orderedDest = Directory.CreateDirectory(Path.Combine(root, "ordered-destination")).FullName;
    WriteMp4(Path.Combine(orderedDest, "01.MP4"), 30, 111);
    WriteMp4(Path.Combine(orderedDest, "starter.mp4"), 1, 112);
    File.WriteAllText(Path.Combine(orderedDest, "2.txt"), "keep notes");
    Directory.CreateDirectory(Path.Combine(orderedDest, "3.mp4"));
    var existingOrdered = Directory.GetFiles(orderedDest).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
    var orderedCandidates = new List<string>();
    for (int i = 0; i < 12; i++)
    {
        string path = Path.Combine(orderedSource, $"clip-{i}.MP4");
        WriteMp4(path, (uint)(12 - i), (byte)(120 + i));
        orderedCandidates.Add(path);
    }
    ClipBatchPreparer.Prepare(orderedSource, orderedDest, TimeSpan.FromSeconds(300), extensions,
        progress, CancellationToken.None, shuffleRandom: new PreserveOrderRandom(), candidates: orderedCandidates);
    Check(Enumerable.Range(4, 12).All(number =>
        ClipIdentifier.ReadIdentity(Path.Combine(orderedDest, $"{number}.MP4"), CancellationToken.None).Duration
            == TimeSpan.FromSeconds(number - 3)),
        "New clips are numbered numerically from shortest to longest, including numbers above 9 and preserving extensions");
    Check(existingOrdered.All(item => File.ReadAllBytes(Path.Combine(orderedDest, item.Key)).SequenceEqual(item.Value))
        && Directory.Exists(Path.Combine(orderedDest, "3.mp4")),
        "Existing numbered and starter clips, notes and folders are untouched; occupied numbers are skipped across extensions and padding");
    Check(orderedCandidates.All(path => File.Exists(Path.Combine(orderedSource, "Backup", Path.GetFileName(path))))
        && Directory.GetFiles(orderedSource).Length == 0,
        "Duration numbering backs up every selected clip under its original filename");

    string shuffleSource = Directory.CreateDirectory(Path.Combine(root, "shuffle-source")).FullName;
    string shuffleDest = Directory.CreateDirectory(Path.Combine(root, "shuffle-destination")).FullName;
    string shuffleNested = Directory.CreateDirectory(Path.Combine(shuffleSource, "nested")).FullName;
    WriteMp4(Path.Combine(shuffleSource, "10.mp4"), 2, 101);
    WriteMp4(Path.Combine(shuffleSource, "20.mp4"), 2, 102);
    WriteMp4(Path.Combine(shuffleSource, "30.mp4"), 2, 103);
    WriteMp4(Path.Combine(shuffleSource, "40.mp4"), 2, 104);
    File.Copy(Path.Combine(shuffleSource, "20.mp4"), Path.Combine(shuffleDest, "existing.mp4"));
    File.Copy(Path.Combine(shuffleSource, "10.mp4"), Path.Combine(shuffleSource, "unnumbered.mp4"));
    File.Copy(Path.Combine(shuffleSource, "10.mp4"), Path.Combine(shuffleNested, "50.mp4"));
    var remainingIdentity = ClipIdentifier.ReadIdentity(Path.Combine(shuffleSource, "10.mp4"), CancellationToken.None);
    var shuffleRandom = new RotateOrderRandom();
    int shuffles = 0, shuffleScans = 0;
    var shuffleProgress = new TestProgress
    {
        OnMessage = message =>
        {
            if (message == "Shuffling source clips before preparing batch...") shuffles++;
            if (message == "Checking prepared batch for duplicates...")
            {
                Check(shuffles == 1 && shuffleRandom.Calls == 3, "All source clips are shuffled once before any batch duplicate scan");
                shuffleScans++;
            }
        }
    };
    string shuffled = ClipBatchPreparer.Prepare(shuffleSource, shuffleDest, TimeSpan.FromSeconds(6),
        extensions, shuffleProgress, CancellationToken.None, ".mp4", shuffleRandom);
    Check(shuffleScans == 2 && shuffles == 1 && shuffleRandom.Calls == 3
        && shuffled.Contains("Moved and backed up: 2") && shuffled.Contains("Duplicates sent to Recycle Bin: 1")
        && Directory.GetFiles(shuffleDest).Select(Path.GetFileName).Order().SequenceEqual(["1.mp4", "2.mp4", "existing.mp4"]),
        "Batch selection and duplicate refill follow the same shuffled source order before numbering the final batch");
    Check(ClipIdentifier.ReadIdentity(Path.Combine(shuffleSource, "10.mp4"), CancellationToken.None) == remainingIdentity
        && File.Exists(Path.Combine(shuffleSource, "unnumbered.mp4")) && File.Exists(Path.Combine(shuffleNested, "50.mp4"))
        && Directory.GetFiles(Path.Combine(shuffleSource, "Backup")).Select(Path.GetFileName).Order().SequenceEqual(["30.mp4", "40.mp4"]),
        "Shuffle preserves unselected source content, excludes unnumbered and nested clips, and backs up selected filenames");

    using var shuffleCts = new CancellationTokenSource();
    var cancelShuffleProgress = new TestProgress
    {
        OnMessage = message => { if (message.StartsWith("Shuffling source clips")) shuffleCts.Cancel(); }
    };
    bool shuffleCancelled = false;
    try
    {
        ClipBatchPreparer.Prepare(shuffleSource, shuffleDest, TimeSpan.FromSeconds(10),
            extensions, cancelShuffleProgress, shuffleCts.Token, ".mp4");
    }
    catch (OperationCanceledException) { shuffleCancelled = true; }
    Check(shuffleCancelled && File.Exists(Path.Combine(shuffleSource, "10.mp4"))
        && Directory.GetFiles(shuffleDest).Length == 3,
        "Cancellation during shuffle stops before cleanup, backup or moving");
    ArchiveChecks.Run(root, WriteMp4);
    DuplicationChecks.Run(root, WriteMp4);
    MainWindowChecks.Run(root, WriteMp4);
    PresetChecks.Run(root, WriteMp4);
    BatchShuffleChecks.Run(root, WriteMp4);
    FolderTrackingChecks.Run(root, WriteMp4);
    Console.WriteLine("All clip operations and folder duration tracking integration checks passed.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
}

static void WriteMp4(string path, uint seconds, byte content)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    void FourCc(string text) => writer.Write(System.Text.Encoding.ASCII.GetBytes(text));
    void UInt32(uint value) => writer.Write(System.Net.IPAddress.HostToNetworkOrder(unchecked((int)value)));
    // Minimal ISO BMFF movie metadata, sufficient for TagLib duration and identity checks.
    UInt32(24); FourCc("ftyp"); FourCc("isom"); UInt32(0); FourCc("isom"); FourCc("mp42");
    UInt32(116); FourCc("moov");
    UInt32(108); FourCc("mvhd");
    UInt32(0); UInt32(0); UInt32(0); // version/flags, creation, modification
    UInt32(1000); UInt32(seconds * 1000);
    UInt32(0x00010000); writer.Write(new byte[] { 1, 0 }); writer.Write(new byte[10]);
    UInt32(0x00010000); UInt32(0); UInt32(0);
    UInt32(0); UInt32(0x00010000); UInt32(0);
    UInt32(0); UInt32(0); UInt32(0x40000000);
    writer.Write(new byte[24]); UInt32(1);
    // An ignored atom varies content without changing size or duration.
    UInt32(10); FourCc("free"); writer.Write(content); writer.Write((byte)0);
}

sealed class TestProgress : IOperationProgress
{
    public Action<int>? OnReport { get; init; }
    public Action<string>? OnMessage { get; init; }
    public CancellationToken CancellationToken => CancellationToken.None;
    public void Report(int current, int total, string message)
    {
        OnReport?.Invoke(current);
        OnMessage?.Invoke(message);
    }
    public void SetIndeterminate(string message) => OnMessage?.Invoke(message);
}

// Control the shuffle so existing cleanup checks retain their deliberate file order.
sealed class PreserveOrderRandom : Random
{
    public override int Next(int maxValue) => maxValue - 1;
}

// Fisher-Yates with zero indices rotates [10, 20, 30, 40] to [20, 30, 40, 10].
sealed class RotateOrderRandom : Random
{
    public int Calls { get; private set; }
    public override int Next(int maxValue)
    {
        Calls++;
        return 0;
    }
}
