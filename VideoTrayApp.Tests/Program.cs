using VideoTrayApp;

// Integration checks use generated AVI headers and isolated, disposable files.
string root = Path.Combine(Path.GetTempPath(), "ClipIdentifierTests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
    string nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
    string destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
    string reference = Path.Combine(source, "reference.avi");
    WriteAvi(reference, 2, 11);
    string duplicate = Path.Combine(nested, "renamed.AVI");
    File.Copy(reference, duplicate);
    string differentContent = Path.Combine(source, "different-content.avi");
    WriteAvi(differentContent, 2, 12);
    WriteAvi(Path.Combine(source, "different-duration.avi"), 3, 11);
    File.WriteAllBytes(Path.Combine(source, "unreadable.avi"), new byte[new FileInfo(reference).Length]);
    var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".avi" };
    var progress = new TestProgress();

    var found = ClipIdentifier.Find(reference, source, extensions, progress, CancellationToken.None);
    Check(found.Matches.SequenceEqual([duplicate]), "Recursive matching excludes reference and rejects same-size, same-duration content with a different hash");
    Check(found.Errors.Count == 1, "Unreadable clips are reported without aborting the search");

    string existing = Path.Combine(destination, "renamed.AVI");
    File.WriteAllText(existing, "keep this destination file");
    string moved = ClipIdentifier.Apply(found, reference, destination, progress, CancellationToken.None);
    Check(!File.Exists(duplicate) && File.Exists(Path.Combine(destination, "renamed__1.AVI")), "Move handles a filename collision");
    Check(File.ReadAllText(existing) == "keep this destination file" && File.Exists(reference), "Existing destination and reference are preserved");
    Check(moved.Contains("Moved: 1"), "Move summary counts completed actions");

    File.Copy(reference, duplicate);
    var changed = ClipIdentifier.Find(reference, source, extensions, progress, CancellationToken.None);
    WriteAvi(duplicate, 2, 99);
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
    var inDestination = new ClipSearchResult(found.Reference, [Path.Combine(destination, "renamed__1.AVI")], []);
    string skipped = ClipIdentifier.Apply(inDestination, reference, destination, progress, CancellationToken.None);
    Check(skipped.Contains("Skipped (already in destination): 1"), "Clips already in destination are left in place");

    // Only this generated duplicate is sent to the Recycle Bin.
    string deleted = ClipIdentifier.Apply(found, reference, null, progress, CancellationToken.None);
    Check(!File.Exists(duplicate) && File.Exists(reference) && deleted.Contains("Sent to Recycle Bin: 1"), "Delete recycles the match and preserves the reference");

    string first = Path.Combine(nested, "first.avi");
    string second = Path.Combine(nested, "second.avi");
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
    string keeper = Path.Combine(cleanupFolder, "a.avi");
    string extra = Path.Combine(cleanupFolder, "z.AVI");
    string extraNested = Path.Combine(cleanupNested, "shuffled.avi");
    WriteAvi(keeper, 2, 41);
    File.Copy(keeper, extra);
    File.Copy(keeper, extraNested);
    string otherKeeper = Path.Combine(cleanupFolder, "b.avi");
    string otherExtra = Path.Combine(cleanupFolder, "y.avi");
    WriteAvi(otherKeeper, 2, 42);
    File.Copy(otherKeeper, otherExtra);
    string durationOnly = Path.Combine(cleanupFolder, "different-duration.avi");
    WriteAvi(durationOnly, 3, 41);
    File.WriteAllBytes(Path.Combine(cleanupFolder, "invalid.avi"), new byte[new FileInfo(keeper).Length]);
    File.Copy(keeper, Path.Combine(cleanupFolder, "ignored.txt"));
    string uniqueSize = Path.Combine(cleanupFolder, "unique.avi");
    WriteAvi(uniqueSize, 2, 41);
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
    WriteAvi(keeper, 2, 77);
    string keeperChanged = ClipDuplicateCleaner.Apply(oneGroup, progress, CancellationToken.None);
    Check(File.Exists(extra) && File.Exists(extraNested) && keeperChanged.Contains("Left untouched: 2"),
        "A changed kept copy protects every duplicate in its group");
    File.Delete(keeper);
    string keeperMissing = ClipDuplicateCleaner.Apply(oneGroup, progress, CancellationToken.None);
    Check(File.Exists(extra) && File.Exists(extraNested) && keeperMissing.Contains("Sent to Recycle Bin: 0"),
        "A missing kept copy protects every duplicate in its group");
    File.Copy(extra, keeper);
    WriteAvi(extraNested, 2, 88);
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
    WriteAvi(Path.Combine(batchSource, "10.avi"), 2, 51);
    File.Copy(Path.Combine(batchSource, "10.avi"), Path.Combine(batchSource, "20.avi"));
    File.Copy(Path.Combine(batchSource, "10.avi"), Path.Combine(batchSource, "30.avi"));
    WriteAvi(Path.Combine(batchSource, "40.avi"), 2, 52);
    WriteAvi(Path.Combine(batchSource, "50.avi"), 2, 53);
    File.Copy(Path.Combine(batchSource, "50.avi"), Path.Combine(batchSource, "60.avi"));
    File.Copy(Path.Combine(batchSource, "10.avi"), Path.Combine(batchBackup, "10.avi"));
    string unnumbered = Path.Combine(batchSource, "shuffled.avi");
    File.Copy(Path.Combine(batchSource, "10.avi"), unnumbered);
    int batchScans = 0;
    var batchProgress = new TestProgress
    {
        OnMessage = message =>
        {
            if (message == "Checking prepared batch for duplicates...") batchScans++;
            if (message.StartsWith("Backing up "))
            {
                Check(batchScans >= 2 && !File.Exists(Path.Combine(batchSource, "20.avi"))
                    && !File.Exists(Path.Combine(batchSource, "30.avi")),
                    "Backup starts only after cleanup, refill and a duplicate-free scan");
            }
        }
    };
    string batchResult = ClipBatchPreparer.Prepare(batchSource, batchDestination, TimeSpan.FromSeconds(6),
        extensions, batchProgress, CancellationToken.None, ".avi");
    Check(batchResult.Contains("Moved and backed up: 3") && batchResult.Contains("Duplicates sent to Recycle Bin: 2")
        && Directory.GetFiles(batchDestination).Select(Path.GetFileName).Order().SequenceEqual(["10.avi", "40.avi", "50.avi"]),
        "Batch refills duration freed by duplicates and moves only unique numbered clips");
    Check(File.Exists(Path.Combine(batchSource, "60.avi")) && File.Exists(unnumbered)
        && File.Exists(Path.Combine(batchBackup, "10.avi")) && File.Exists(Path.Combine(batchBackup, "10__1.avi"))
        && !File.Exists(Path.Combine(batchBackup, "20.avi")),
        "Unselected clips and existing backups are preserved; only final unique clips get new backups");

    string refillSource = Directory.CreateDirectory(Path.Combine(root, "refill-source")).FullName;
    string refillDestination = Directory.CreateDirectory(Path.Combine(root, "refill-destination")).FullName;
    WriteAvi(Path.Combine(refillSource, "10.avi"), 2, 61);
    File.Copy(Path.Combine(refillSource, "10.avi"), Path.Combine(refillDestination, "a.avi"));
    File.Copy(Path.Combine(refillSource, "10.avi"), Path.Combine(refillDestination, "z.avi"));
    WriteAvi(Path.Combine(refillSource, "20.avi"), 2, 62);
    File.Copy(Path.Combine(refillSource, "20.avi"), Path.Combine(refillSource, "30.avi"));
    WriteAvi(Path.Combine(refillSource, "40.avi"), 2, 63);
    int refillScans = 0;
    var refillProgress = new TestProgress
    {
        OnMessage = message => { if (message == "Checking prepared batch for duplicates...") refillScans++; }
    };
    string refilled = ClipBatchPreparer.Prepare(refillSource, refillDestination, TimeSpan.FromSeconds(6),
        extensions, refillProgress, CancellationToken.None, ".avi");
    Check(refillScans == 3 && refilled.Contains("Duplicates sent to Recycle Bin: 3")
        && File.Exists(Path.Combine(refillDestination, "a.avi")) && !File.Exists(Path.Combine(refillDestination, "z.avi"))
        && File.Exists(Path.Combine(refillDestination, "20.avi")) && File.Exists(Path.Combine(refillDestination, "40.avi")),
        "Destination copies take priority and duplicates introduced during refill trigger another cleanup cycle");

    string exhaustedSource = Directory.CreateDirectory(Path.Combine(root, "exhausted-source")).FullName;
    string exhaustedDestination = Directory.CreateDirectory(Path.Combine(root, "exhausted-destination")).FullName;
    WriteAvi(Path.Combine(exhaustedSource, "10.avi"), 2, 71);
    File.Copy(Path.Combine(exhaustedSource, "10.avi"), Path.Combine(exhaustedSource, "20.avi"));
    string exhausted = ClipBatchPreparer.Prepare(exhaustedSource, exhaustedDestination, TimeSpan.FromSeconds(10),
        extensions, progress, CancellationToken.None, ".avi");
    Check(exhausted.Contains("Moved and backed up: 1") && Directory.GetFiles(exhaustedDestination).Length == 1,
        "Exhausted source completes with a smaller duplicate-free batch");

    string failedSource = Directory.CreateDirectory(Path.Combine(root, "failed-source")).FullName;
    string failedDestination = Directory.CreateDirectory(Path.Combine(root, "failed-destination")).FullName;
    WriteAvi(Path.Combine(failedSource, "10.avi"), 2, 81);
    File.Copy(Path.Combine(failedSource, "10.avi"), Path.Combine(failedSource, "20.avi"));
    bool changedBatch = false;
    var changedBatchProgress = new TestProgress
    {
        OnMessage = message =>
        {
            if (!changedBatch && message == "Verifying duplicate: 20.avi")
            {
                changedBatch = true;
                WriteAvi(Path.Combine(failedSource, "20.avi"), 2, 82);
            }
        }
    };
    bool cleanupFailed = false;
    try
    {
        ClipBatchPreparer.Prepare(failedSource, failedDestination, TimeSpan.FromSeconds(6),
            extensions, changedBatchProgress, CancellationToken.None, ".avi");
    }
    catch (IOException) { cleanupFailed = true; }
    Check(cleanupFailed && !Directory.Exists(Path.Combine(failedSource, "Backup"))
        && Directory.GetFiles(failedDestination).Length == 0 && Directory.GetFiles(failedSource).Length == 2,
        "Cleanup verification failure stops before backup or moving without retrying indefinitely");

    File.Copy(Path.Combine(failedSource, "10.avi"), Path.Combine(failedSource, "20.avi"), overwrite: true);
    using var batchCts = new CancellationTokenSource();
    var cancelBatchProgress = new TestProgress
    {
        OnMessage = message => { if (message.StartsWith("Sent to Recycle Bin: 1")) batchCts.Cancel(); }
    };
    bool batchCancelled = false;
    try
    {
        ClipBatchPreparer.Prepare(failedSource, failedDestination, TimeSpan.FromSeconds(6),
            extensions, cancelBatchProgress, batchCts.Token, ".avi");
    }
    catch (OperationCanceledException) { batchCancelled = true; }
    Check(batchCancelled && File.Exists(Path.Combine(failedSource, "10.avi"))
        && !Directory.Exists(Path.Combine(failedSource, "Backup")) && Directory.GetFiles(failedDestination).Length == 0,
        "Cancellation after recycling stops batch preparation before backup and moving");
    bool invalidDestination = false;
    try
    {
        ClipBatchPreparer.Prepare(batchSource, batchBackup, TimeSpan.FromSeconds(6),
            extensions, progress, CancellationToken.None, ".avi");
    }
    catch (IOException) { invalidDestination = true; }
    Check(invalidDestination, "Batch preparation rejects using the Backup folder as destination");
    string invalidSource = Directory.CreateDirectory(Path.Combine(root, "invalid-batch-source")).FullName;
    string invalidDest = Directory.CreateDirectory(Path.Combine(root, "invalid-batch-dest")).FullName;
    File.WriteAllBytes(Path.Combine(invalidSource, "10.avi"), [0, 1, 2]);
    bool unreadableBatch = false;
    try
    {
        ClipBatchPreparer.Prepare(invalidSource, invalidDest, TimeSpan.FromSeconds(6),
            extensions, progress, CancellationToken.None, ".avi");
    }
    catch (Exception ex) when (ex is not OperationCanceledException) { unreadableBatch = true; }
    Check(unreadableBatch && !Directory.Exists(Path.Combine(invalidSource, "Backup"))
        && Directory.GetFiles(invalidDest).Length == 0 && File.Exists(Path.Combine(invalidSource, "10.avi")),
        "Unreadable clips stop preparation before backup and moving");

    string collisionSource = Directory.CreateDirectory(Path.Combine(root, "collision-source")).FullName;
    string collisionDest = Directory.CreateDirectory(Path.Combine(root, "collision-dest")).FullName;
    WriteAvi(Path.Combine(collisionSource, "10.avi"), 2, 91);
    WriteAvi(Path.Combine(collisionDest, "10.avi"), 2, 92);
    var collisionIdentity = ClipIdentifier.ReadIdentity(Path.Combine(collisionDest, "10.avi"), CancellationToken.None);
    string collision = ClipBatchPreparer.Prepare(collisionSource, collisionDest, TimeSpan.FromSeconds(6),
        extensions, progress, CancellationToken.None, ".avi");
    Check(collision.Contains("Failed to back up or move: 1") && File.Exists(Path.Combine(collisionSource, "10.avi"))
        && ClipIdentifier.ReadIdentity(Path.Combine(collisionDest, "10.avi"), CancellationToken.None) == collisionIdentity,
        "A distinct destination filename collision preserves both clips and reports the failed move");
    Console.WriteLine("All clip identification, duplicate cleanup and batch preparation integration checks passed.");
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

static void WriteAvi(string path, uint frames, byte content)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    void FourCc(string text) => writer.Write(System.Text.Encoding.ASCII.GetBytes(text));
    FourCc("RIFF"); writer.Write(80u); FourCc("AVI ");
    FourCc("LIST"); writer.Write(192u); FourCc("hdrl");
    FourCc("avih"); writer.Write(56u);
    writer.Write(1_000_000u); // one second per frame
    writer.Write(0u); writer.Write(0u); writer.Write(0u);
    writer.Write(frames);
    writer.Write(0u); writer.Write(0u); writer.Write(0u);
    writer.Write(1u); writer.Write(1u); // width and height
    writer.Write(new byte[16]);
    FourCc("LIST"); writer.Write(116u); FourCc("strl");
    FourCc("strh"); writer.Write(56u);
    FourCc("vids"); FourCc("DIB ");
    writer.Write(0u); writer.Write((ushort)0); writer.Write((ushort)0);
    writer.Write(0u); writer.Write(1u); writer.Write(1u); writer.Write(0u);
    writer.Write(frames); writer.Write(3u); writer.Write(uint.MaxValue); writer.Write(0u);
    writer.Write((short)0); writer.Write((short)0); writer.Write((short)1); writer.Write((short)1);
    FourCc("strf"); writer.Write(40u);
    writer.Write(40u); writer.Write(1); writer.Write(1);
    writer.Write((ushort)1); writer.Write((ushort)24);
    writer.Write(0u); writer.Write(3u); writer.Write(0); writer.Write(0); writer.Write(0u); writer.Write(0u);
    // An ignored chunk provides differing content without changing size/duration.
    FourCc("JUNK"); writer.Write(2u); writer.Write(content); writer.Write((byte)0);
    stream.Position = 4;
    writer.Write((uint)(stream.Length - 8));
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
