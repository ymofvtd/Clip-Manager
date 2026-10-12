using System.Security.Cryptography;
using VideoTrayApp;

internal static class BatchShuffleChecks
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".avi" };

    internal static void Run(string parent, Action<string, uint, byte> writeVideo)
    {
        string root = Directory.CreateDirectory(Path.Combine(parent, "batch-shuffle")).FullName;
        string first = Directory.CreateDirectory(Path.Combine(root, "folder-1")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(root, "folder-2", "nested")).FullName;
        string backup = Directory.CreateDirectory(Path.Combine(root, "Backup")).FullName;
        string nestedBackup = Directory.CreateDirectory(Path.Combine(nested, "Backup")).FullName;
        writeVideo(Path.Combine(first, "same.avi"), 12, 1);
        writeVideo(Path.Combine(nested, "same.avi"), 8, 2);
        writeVideo(Path.Combine(root, "loose.avi"), 10, 3);
        writeVideo(Path.Combine(root, "long.avi"), 35, 4);
        File.Copy(Path.Combine(first, "same.avi"), Path.Combine(root, "duplicate.avi"));
        writeVideo(Path.Combine(backup, "saved.avi"), 20, 5);
        writeVideo(Path.Combine(nestedBackup, "saved.avi"), 20, 6);
        File.WriteAllText(Path.Combine(first, "0001.avi" + ".txt"), "keep notes");
        // An occupied video-named directory must also survive numbering.
        Directory.CreateDirectory(Path.Combine(first, "0002.avi"));
        File.WriteAllText(Path.Combine(root, "batch_0001"), "occupied batch name");
        var beforeContent = Contents(root);
        string result = ClipBatchShuffler.Run(root, TimeSpan.FromSeconds(30), Extensions,
            new TestProgress(), CancellationToken.None, new RotateOrderRandom());
        Check(result.Contains("Clips reshuffled: 5") && result.Contains("Batch folders: 3")
            && result.Contains("own folders: 1"), "All root/nested clips, including duplicate copies, enter duration-limited batches");
        Check(beforeContent.SequenceEqual(Contents(root)), "Shuffle preserves every video byte, collision and duplicate copy");
        Check(!Directory.GetFiles(root, "*.avi").Any() && Directory.Exists(Path.Combine(root, "batch_0002"))
            && File.ReadAllText(Path.Combine(root, "batch_0001")) == "occupied batch name"
            && Directory.Exists(Path.Combine(first, "0002.avi"))
            && File.ReadAllText(Path.Combine(first, "0001.avi.txt")) == "keep notes",
            "Loose clips are batched, occupied folder/file names are skipped, and notes and directories survive");
        Check(File.Exists(Path.Combine(backup, "saved.avi")) && File.Exists(Path.Combine(nestedBackup, "saved.avi"))
            && Directory.GetFiles(nested, "*.avi").Length == 0
            && !Directory.GetFiles(root, "*.pending", SearchOption.AllDirectories).Any(),
            "Backup trees stay excluded, nested clips are included, and staging leaves no temporary files");
        AssertLimits(root, TimeSpan.FromSeconds(30));

        writeVideo(Path.Combine(root, "new-loose.avi"), 9, 7);
        var allContent = Contents(root);
        var beforePaths = Snapshot(root);
        ClipBatchShuffler.Run(root, TimeSpan.FromSeconds(30), Extensions, new TestProgress(), CancellationToken.None, new Random(124));
        Check(allContent.SequenceEqual(Contents(root)) && !File.Exists(Path.Combine(root, "new-loose.avi"))
            && !beforePaths.SequenceEqual(Snapshot(root)), "A repeat run reshuffles existing batches and incorporates newly added loose clips");
        AssertLimits(root, TimeSpan.FromSeconds(30));

        // Verify cancellation and an I/O failure in both passes restore exact paths and bytes.
        foreach (string phase in new[] { "Staging clip 2/", "Filling batch 2/", "failure" })
        {
            var snapshot = Snapshot(root);
            var folders = Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order().ToArray();
            using var cts = new CancellationTokenSource();
            bool triggered = false, failed = false;
            var progress = new TestProgress { OnMessage = message =>
            {
                if (phase == "failure" && message.StartsWith("Filling batch 2/"))
                {
                    triggered = true;
                    throw new IOException("Injected destination failure");
                }
                if (message.StartsWith(phase)) { triggered = true; cts.Cancel(); }
            } };
            try { ClipBatchShuffler.Run(root, TimeSpan.FromSeconds(1), Extensions, progress, cts.Token, new Random(6)); }
            catch (OperationCanceledException) { failed = true; }
            catch (IOException) when (phase == "failure") { failed = true; }
            Check(triggered && failed && snapshot.SequenceEqual(Snapshot(root))
                && folders.SequenceEqual(Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order()),
                $"{phase}: rollback restores exact paths/content and removes new batch folders");
        }
        var original = Snapshot(root);
        using (var transaction = new OperationTransaction())
        {
            ClipBatchShuffler.Run(root, TimeSpan.FromSeconds(1), Extensions, new TestProgress(), CancellationToken.None, new Random(3));
            transaction.Rollback();
        }
        Check(original.SequenceEqual(Snapshot(root)), "Saved-preset transactions can reverse the entire batch shuffle");
        File.WriteAllText(Path.Combine(root, "unreadable.avi"), "invalid video");
        original = Snapshot(root);
        bool invalid = false;
        try { ClipBatchShuffler.Run(root, TimeSpan.FromSeconds(30), Extensions, new TestProgress(), CancellationToken.None); }
        catch (Exception) { invalid = true; }
        Check(invalid && original.SequenceEqual(Snapshot(root)), "Unreadable duration stops the shuffle before any moves");

        var preset = new ActionPreset { Action = PresetAction.ShuffleBatches, Name = "30m remix", UseWorkingFolder = false,
            Target = root, DurationMinutes = 30 };
        preset.Validate(root);
        var store = new PresetStore { Presets = [preset], Defaults = new() { [preset.Action] = preset.Id } };
        string config = Path.Combine(parent, "batch-shuffle-presets.json");
        store.Save(config);
        Check(PresetStore.Load(config).DefaultFor(PresetAction.ShuffleBatches)?.DurationMinutes == 30
            && preset.Summary(root).Contains("No backups or duplicate deletion"), "Shuffle Batches presets retain archive, duration and behavior");

        string large = Directory.CreateDirectory(Path.Combine(parent, "batch-shuffle-4000")).FullName;
        for (int i = 0; i < 4000; i++)
        {
            string folder = i % 4 == 0 ? large : Directory.CreateDirectory(Path.Combine(large, $"archive-{i % 4}")).FullName;
            writeVideo(Path.Combine(folder, $"clip-{i}.avi"), 60, (byte)(i % 256));
        }
        var largeBefore = Contents(large);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        result = ClipBatchShuffler.Run(large, TimeSpan.FromMinutes(30), Extensions,
            new TestProgress(), CancellationToken.None, new Random(77));
        Check(result.Contains("Clips reshuffled: 4000") && result.Contains("Batch folders: 134")
            && largeBefore.SequenceEqual(Contents(large)) && !Directory.GetFiles(large, "*.avi").Any()
            && !Directory.Exists(Path.Combine(large, "Backup")),
            $"4,000-clip archive preserves all copies and fills 134 batches without backups ({timer.Elapsed.TotalSeconds:F1}s on generated clips)");
        AssertLimits(large, TimeSpan.FromMinutes(30));
    }

    private static IEnumerable<string> VideoPaths(string root) => Directory.GetFiles(root, "*.avi", SearchOption.AllDirectories);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string[] Contents(string root) => VideoPaths(root).Select(Hash).Order().ToArray();
    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Hash(path)).Order().ToArray();
    private static void AssertLimits(string root, TimeSpan limit)
    {
        bool valid = true;
        foreach (string folder in Directory.GetDirectories(root))
        {
            if (Path.GetFileName(folder) == "Backup") continue;
            var durations = Directory.GetFiles(folder, "*.avi").Select(path =>
            {
                using var video = TagLib.File.Create(path);
                return video.Properties.Duration;
            }).ToArray();
            if (durations.Length == 0) continue;
            valid &= durations.Sum(d => d.TotalSeconds) <= limit.TotalSeconds
                || durations.Length == 1 && durations[0] > limit;
        }
        Check(valid, "Every batch meets the duration limit or contains one oversized clip");
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }
}
