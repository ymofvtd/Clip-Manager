using VideoTrayApp;

internal static class Mp4Checks
{
    internal static void Run(string parent, Action<string, uint, byte> write)
    {
        string source = Directory.CreateDirectory(Path.Combine(parent, "mp4-only")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(parent, "mp4-only-destination")).FullName;
        string reference = Path.Combine(source, "1.mp4");
        string duplicate = Path.Combine(source, "2.MP4");
        write(reference, 2, 10); File.Copy(reference, duplicate);
        string[] ignored = [".avi", ".mov", ".mkv", ".m4v", ".wmv", ".webm", ".mxf", ".mts", ".m2ts", ".mpg", ".mpeg", ".3gp", ".flv", ".txt"];
        var extensions = new HashSet<string>(ignored.Append(".mp4"), StringComparer.OrdinalIgnoreCase);
        foreach (string extension in ignored) File.Copy(reference, Path.Combine(source, "ignored" + extension));
        var untouched = Directory.GetFiles(source).Where(p => !VideoFiles.IsMp4(p)).ToDictionary(p => p, File.ReadAllBytes);
        var progress = new TestProgress();
        var ct = CancellationToken.None;

        Check(MainShell.VideoExtensions.SetEquals([".mp4"]) && MainShell.VideoExtensions.Contains(".MP4"),
            "The app format list supports only MP4, including uppercase extensions");
        var errors = new List<string>();
        Check(ArchivePaths.Videos([source], extensions, errors, ct).Order().SequenceEqual(new[] { reference, duplicate }.Order()) && errors.Count == 0,
            "Recursive enumeration excludes every other format even with a legacy format list");
        Check(ClipIdentifier.Find(reference, source, extensions, progress, ct).Matches.SequenceEqual([duplicate]),
            "Identify clip matches uppercase MP4 and excludes identical content with other extensions");
        bool rejected = false;
        try { ClipIdentifier.Find(Path.Combine(source, "ignored.mov"), source, extensions, progress, ct); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Pasted non-MP4 reference paths are rejected before scanning");
        var preset = new ActionPreset { Action = PresetAction.IdentifyClip, ReferenceClip = Path.Combine(source, "ignored.mov") };
        rejected = false;
        try { preset.ValidateForSave(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Identify presets reject non-MP4 references");
        Check(ClipDuplicateCleaner.Find(source, extensions, progress, ct).DuplicateCount == 1
            && ClipDuplicateCleaner.Find(Directory.GetFiles(source), progress, ct).DuplicateCount == 1,
            "Both folder and explicit-file duplicate scans ignore other formats");
        string comparison = Directory.CreateDirectory(Path.Combine(parent, "mp4-only-comparison")).FullName;
        File.Copy(reference, Path.Combine(comparison, "copy.mp4"));
        File.Copy(reference, Path.Combine(comparison, "copy.avi"));
        var sought = ClipDuplicationSeeker.Find(source, comparison, true, extensions, progress, ct);
        Check(sought.Groups.Count == 1 && sought.Groups.SelectMany(g => g.Copies).Count() == 3
            && sought.Groups.SelectMany(g => g.Copies).All(c => VideoFiles.IsMp4(c.Path)),
            "Seek duplications only includes MP4 paths from both directories");
        foreach (var action in new[] { PresetAction.ShuffleAndName, PresetAction.NameByTens, PresetAction.ShuffleRandom })
        {
            var rename = RenamePlan.Analyze(source, action, 0, 0, "", 12, extensions, progress, ct, new Random(2));
            Check(rename.Changes.Count == 2 && rename.Changes.All(c => VideoFiles.IsMp4(c.File.Path) && VideoFiles.IsMp4(c.Destination)),
                action + " plans only MP4 files");
        }
        var shuffle = ClipBatchShuffler.Analyze(source, TimeSpan.FromSeconds(10), extensions, progress, ct, new Random(2));
        Check(shuffle.Changes.Count == 2 && shuffle.Changes.All(c => VideoFiles.IsMp4(c.Clip.Path)), "Shuffle Batches ignores all other formats");
        foreach (var selection in Enum.GetValues<BatchSelection>())
        {
            var batch = PrepareBatchPlan.Analyze(new ActionPreset { Destination = destination, Selection = selection }, source, extensions, progress, ct, new Random(2));
            Check(batch.Changes.Count > 0 && batch.Changes.All(c => VideoFiles.IsMp4(c.File.Path))
                && batch.DuplicateGroups.SelectMany(g => g.Duplicates.Prepend(g.Keep)).All(VideoFiles.IsMp4),
                "Prepare Batch " + selection + " selects and cleans only MP4 files");
        }
        using (var tracker = new FolderDurationTracker(extensions))
        {
            tracker.SetFolders([source]);
            Check(SpinWait.SpinUntil(() => tracker.Totals.TryGetValue(source, out var total) && total.Duration == TimeSpan.FromSeconds(4), 10000),
                "Duration tracking totals MP4 files only in a mixed-format folder");
        }
        var archive = ClipArchiver.Run(new ArchiveSettings { Sources = [source], Destination = destination, Speed = ArchiveSpeed.Fast, SettlingSeconds = 0 },
            Path.Combine(parent, "mp4-only-data"), extensions, progress, ct);
        Check(archive.Copied == 1 && archive.AlreadyArchived == 1 && archive.Errors.Count == 0
            && Directory.GetFiles(destination).All(VideoFiles.IsMp4), "Archive publishes only MP4 copies from mixed-format sources");
        string batchDestination = Directory.CreateDirectory(Path.Combine(parent, "mp4-only-manual-batch")).FullName;
        ClipBatchPreparer.Prepare(source, batchDestination, TimeSpan.FromSeconds(2), extensions, progress, ct,
            candidates: [reference, Path.Combine(source, "ignored.mov")]);
        Check(Directory.GetFiles(batchDestination).Length == 1 && Directory.GetFiles(batchDestination).All(VideoFiles.IsMp4)
            && !File.Exists(Path.Combine(source, "Backup", "ignored.mov")), "Legacy Prepare Batch filters explicit candidates before backup and moving");
        Check(untouched.All(item => File.ReadAllBytes(item.Key).SequenceEqual(item.Value)), "Mixed-format operations leave all non-MP4 content untouched");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }
}
