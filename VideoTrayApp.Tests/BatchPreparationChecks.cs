using System.Security.Cryptography;
using VideoTrayApp;

internal static class BatchPreparationChecks
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4" };

    internal static void Run(string parent, Action<string, uint, byte> write)
    {
        string root = Directory.CreateDirectory(Path.Combine(parent, "prepare-shuffles")).FullName;
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(root, "c_10")).FullName;
        string backup = Directory.CreateDirectory(Path.Combine(source, "Backup")).FullName;
        for (int i = 1; i <= 6; i++)
            write(Path.Combine(source, $"{i * 10}.mp4"), i == 2 ? 4u : i == 3 ? 2u : 3u, (byte)i);
        File.Copy(Path.Combine(source, "20.mp4"), Path.Combine(destination, "long-starter.mp4"));
        File.WriteAllText(Path.Combine(destination, "2.txt"), "keep notes");
        Directory.CreateDirectory(Path.Combine(destination, "4.mp4"));
        File.WriteAllText(Path.Combine(backup, "30.mp4"), "existing backup");
        write(Path.Combine(source, "unnumbered.mp4"), 2, 80);
        string nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
        write(Path.Combine(nested, "10.mp4"), 2, 81);
        var candidates = Enumerable.Range(1, 6).Select(i => Path.Combine(source, $"{i * 10}.mp4")).ToList();
        var hashes = candidates.ToDictionary(p => p, Hash, StringComparer.OrdinalIgnoreCase);
        var original = Snapshot(root);
        PrepareBatchPlan Analyze() => PrepareBatchPlan.AnalyzeCandidates(source, root, new(destination, false),
            TimeSpan.FromSeconds(9), candidates, Extensions, new TestProgress(), CancellationToken.None, new RotateOrderRandom());
        var plan = Analyze();
        Check(original.SequenceEqual(Snapshot(root)) && plan.SourceShuffle.Count == 6 && plan.RemainingShuffle.Count == 3,
            "Analysis freezes both source shuffles and duplicate refill without changing files");
        Check(plan.SourceShuffle.Any(c => c.File.Path != c.Destination)
            && plan.RemainingShuffle.Any(c => c.Destination != plan.SourceShuffle.Single(s => s.File.Path == c.File.Path).Destination),
            "Both reviewed source shuffles change the filename order, including clips outside the prepared batch");

        // Failure after the first shuffle restores the original paths, not the intermediate shuffled names.
        using (var cancellation = new CancellationTokenSource())
        {
            bool cancelled = false;
            try
            {
                plan.Execute(new TestProgress { OnMessage = message =>
                {
                    if (message.StartsWith("Source filenames shuffled;")) cancellation.Cancel();
                } }, cancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && original.SequenceEqual(Snapshot(root)),
                "Cancellation after the first physical shuffle restores all filenames and bytes");
        }
        // Rollback must cover target renumbering, backups, staged duplicate recycling and both source shuffles.
        foreach (bool cancel in new[] { true, false })
        {
            plan = Analyze();
            bool after = false, failed = false;
            string failure = "";
            using var cancellation = new CancellationTokenSource();
            try
            {
                plan.Execute(new TestProgress { OnMessage = message =>
                {
                    if (message.StartsWith("Reshuffling remaining source filenames")) after = true;
                    if (after && message.StartsWith("Renaming reviewed files"))
                    {
                        if (cancel) cancellation.Cancel();
                        else throw new IOException("Injected second-shuffle failure");
                    }
                } }, cancellation.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { failed = true; failure = ex.ToString(); }
            Check(after && failed && original.SequenceEqual(Snapshot(root)),
                $"Second-shuffle {(cancel ? "cancellation" : "failure")} restores source order, target order, duplicates and backups"
                + (after && failed && original.SequenceEqual(Snapshot(root)) ? "" : "\n" + failure + "\n" + string.Join("\n", Snapshot(root).Except(original))));
        }
        plan = Analyze();
        bool beforeSeen = false, afterSeen = false;
        string result = plan.Execute(new TestProgress { OnMessage = message =>
        {
            if (message.StartsWith("Source filenames shuffled;"))
            {
                beforeSeen = true;
                Check(plan.SourceShuffle.All(c => Hash(c.Destination) == hashes[c.File.Path]),
                    "All eligible source clips are physically shuffled before duplicate cleanup and backup");
                Check(!File.Exists(Path.Combine(backup, "40.mp4")), "Batch backups start after the first source shuffle");
            }
            if (message == "Source reshuffle completed.")
            {
                afterSeen = true;
                Check(plan.RemainingShuffle.All(c => Hash(c.Destination) == hashes[c.File.Path]),
                    "Remaining source clips are physically shuffled again after target preparation");
            }
        } }, CancellationToken.None);
        Check(beforeSeen && afterSeen && result.Contains("Moved and backed up: 2")
            && result.Contains("Duplicates sent to Recycle Bin: 1"), "Prepare runs both source shuffles around duplicate refill, backup and moving");
        Check(plan.Changes.All(c => Hash(c.Destination) == hashes[c.File.Path] && Hash(c.Backup!) == hashes[c.File.Path])
            && File.ReadAllText(Path.Combine(backup, "30.mp4")) == "existing backup"
            && plan.Changes.Single(c => Path.GetFileName(c.File.Path) == "30.mp4").Backup == Path.Combine(backup, "30__1.mp4"),
            "Backups retain the original pre-shuffle filenames/content and safely suffix occupied names");
        Check(new[] { "1.mp4", "3.mp4", "5.mp4" }.Select(p => ClipIdentifier.ReadIdentity(Path.Combine(destination, p), CancellationToken.None).Duration)
            .SequenceEqual(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4) })
            && !File.Exists(Path.Combine(destination, "long-starter.mp4")),
            "The entire target batch, including existing starters, is numbered shortest to longest around occupied numbers");
        Check(File.Exists(Path.Combine(source, "unnumbered.mp4")) && File.Exists(Path.Combine(nested, "10.mp4"))
            && File.ReadAllText(Path.Combine(destination, "2.txt")) == "keep notes" && Directory.Exists(Path.Combine(destination, "4.mp4")),
            "Selection exclusions, source subfolders and target non-video entries remain intact");

        ReviewedInventory(root, write);
        RecursiveSources(root, write);
        RepeatedManualPreparation(root, write);
    }

    private static void ReviewedInventory(string root, Action<string, uint, byte> write)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "review-source")).FullName;
        string output = Directory.CreateDirectory(Path.Combine(root, "review-output")).FullName;
        for (int i = 1; i <= 5; i++) write(Path.Combine(source, $"{i}.mp4"), 35, (byte)(90 + i));
        var preset = new ActionPreset { Target = source, Destination = output, Selection = BatchSelection.AllVideos, DurationMinutes = 1 };
        PrepareBatchPlan Analyze() => PrepareBatchPlan.Analyze(preset, source, Extensions, new TestProgress(), CancellationToken.None, new RotateOrderRandom());
        var plan = Analyze();
        write(Path.Combine(source, "new.mp4"), 35, 99);
        var original = Snapshot(source);
        ExpectFailure(() => plan.Execute(new TestProgress(), CancellationToken.None));
        Check(original.SequenceEqual(Snapshot(source)) && Directory.GetFileSystemEntries(output).Length == 0,
            "New source clips invalidate the reviewed shuffle before any renames or output folders");
        File.Delete(Path.Combine(source, "new.mp4"));
        plan = Analyze();
        var unselected = plan.RemainingShuffle.First().File;
        string replacement = unselected.Path + ".replacement";
        File.Copy(unselected.Path, replacement);
        File.Delete(unselected.Path);
        File.Move(replacement, unselected.Path);
        File.SetCreationTimeUtc(unselected.Path, unselected.Created);
        File.SetLastWriteTimeUtc(unselected.Path, unselected.LastWrite);
        original = Snapshot(source);
        ExpectFailure(() => plan.Execute(new TestProgress(), CancellationToken.None));
        Check(original.SequenceEqual(Snapshot(source)) && Directory.GetFileSystemEntries(output).Length == 0,
            "Replacing an unselected source clip invalidates the full-folder shuffle even with identical bytes/timestamps");
    }

    private static void RecursiveSources(string root, Action<string, uint, byte> write)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "recursive-source")).FullName;
        string output = Directory.CreateDirectory(Path.Combine(source, "output")).FullName;
        string excluded = Directory.CreateDirectory(Path.Combine(source, "Backup")).FullName;
        write(Path.Combine(excluded, "skip.mp4"), 1, 110);
        write(Path.Combine(output, "skip.mp4"), 1, 111);
        for (int folder = 0; folder < 2; folder++)
        {
            string path = folder == 0 ? source : Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
            for (int i = 1; i <= 4; i++) write(Path.Combine(path, $"clip-{i}{(i % 2 == 0 ? ".MP4" : ".mp4")}"), 30, (byte)(120 + folder * 10 + i));
        }
        var preset = new ActionPreset { Target = source, Destination = output, Selection = BatchSelection.Archives, DurationMinutes = 1 };
        var plan = PrepareBatchPlan.Analyze(preset, source, Extensions, new TestProgress(), CancellationToken.None, new RotateOrderRandom());
        var hashes = plan.SourceShuffle.ToDictionary(c => c.File.Path, c => Hash(c.File.Path));
        Check(plan.SourceShuffle.Count == 8 && plan.SourceShuffle.All(c => Path.GetDirectoryName(c.File.Path) == Path.GetDirectoryName(c.Destination)
            && Path.GetExtension(c.File.Path) == Path.GetExtension(c.Destination)),
            "Recursive source shuffles preserve each folder and extension casing, excluding Backup and destination trees");
        plan.Execute(new TestProgress(), CancellationToken.None);
        Check(plan.RemainingShuffle.All(c => Hash(c.Destination) == hashes[c.File.Path])
            && File.Exists(Path.Combine(excluded, "skip.mp4")) && File.Exists(Path.Combine(output, "skip.mp4")),
            "Recursive preparation applies the second source shuffle without touching excluded trees");
    }

    private static void RepeatedManualPreparation(string root, Action<string, uint, byte> write)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "repeat-source")).FullName;
        for (int i = 1; i <= 8; i++) write(Path.Combine(source, $"{i * 10}.mp4"), (uint)(i + 1), (byte)(150 + i));
        for (int run = 1; run <= 2; run++)
        {
            string destination = Directory.CreateDirectory(Path.Combine(root, "repeat-output-" + run)).FullName;
            int before = 0, after = 0;
            ClipBatchPreparer.Prepare(source, destination, TimeSpan.FromSeconds(10), Extensions,
                new TestProgress { OnMessage = message =>
                {
                    if (message.StartsWith("Source filenames shuffled;")) before++;
                    if (message == "Source reshuffle completed.") after++;
                } }, CancellationToken.None, shuffleRandom: new RotateOrderRandom());
            var durations = Directory.GetFiles(destination).OrderBy(p => int.Parse(Path.GetFileNameWithoutExtension(p)))
                .Select(p => ClipIdentifier.ReadIdentity(p, CancellationToken.None).Duration).ToArray();
            Check(before == 1 && after == 1 && durations.SequenceEqual(durations.Order()),
                $"Prepare call {run} automatically shuffles the source before/after and orders the target by duration");
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string[] Snapshot(string root) => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(root, p) + ":" + (File.Exists(p) ? Hash(p) : "folder")).Order().ToArray();
    private static void ExpectFailure(Action action)
    {
        try { action(); } catch (IOException) { return; }
        throw new Exception("Expected preparation to reject a changed review");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
