using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VideoTrayApp;

internal static class RenameChecks
{
    internal static void Run(string parent, Action<string, uint, byte> write)
    {
        DurationOrder(parent, write);
        var progress = new TestProgress();
        var ct = CancellationToken.None;
        RenamePlan Analyze(string folder, PresetAction mode = PresetAction.ShuffleAndName) =>
            RenamePlan.Analyze(folder, mode, 100, 4, "clip_", 12, MainShell.VideoExtensions, progress, ct, new Random(42));
        string source = Directory.CreateDirectory(Path.Combine(parent, "rename-checks")).FullName;
        for (int i = 0; i < 4; i++) write(Path.Combine(source, $"original-{i}" + (i == 0 ? ".MP4" : ".mp4")), 2, (byte)i);

        // Warm the rename paths before measuring process I/O; JIT/assembly loading is unrelated.
        foreach (var mode in new[] { PresetAction.ShuffleAndName, PresetAction.NameByTens, PresetAction.ShuffleRandom })
            Analyze(source, mode).Execute(progress, ct);
        foreach (string path in Directory.GetFiles(source))
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write)) stream.SetLength(8 * 1024 * 1024);
        File.WriteAllText(Path.Combine(source, "notes.txt"), "preserve notes");
        string nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
        write(Path.Combine(nested, "leave.mp4"), 2, 99);
        var excluded = new[] { Path.Combine(source, "notes.txt"), Path.Combine(nested, "leave.mp4") }.ToDictionary(p => p, Hash);

        foreach (var mode in new[] { PresetAction.ShuffleAndName, PresetAction.NameByTens, PresetAction.ShuffleRandom })
        {
            var originals = Snapshot(source);
            ulong before = ReadBytes();
            var timer = Stopwatch.StartNew();
            var plan = Analyze(source, mode);
            long previewMs = timer.ElapsedMilliseconds;
            ulong previewReads = ReadBytes() - before;
            Check(plan.Changes.Count == 4 && SameSnapshot(source, originals), mode + ": preview preserves files and excludes notes and subfolders");
            before = ReadBytes(); timer.Restart();
            plan.Execute(progress, ct);
            long executeMs = timer.ElapsedMilliseconds;
            ulong executionReads = ReadBytes() - before;
            Console.WriteLine($"MEASURE: {mode}, 32 MiB: preview {previewMs} ms / {previewReads} bytes read; execution {executeMs} ms / {executionReads} bytes read.");
            Check(previewReads < 256 * 1024 && executionReads < 256 * 1024, mode + ": preview and execution avoid reading full video contents");
            Check(plan.Changes.All(c => Hash(c.Destination) == originals[c.File.Path]
                && Path.GetExtension(c.Destination) == Path.GetExtension(c.File.Path)), mode + ": exact reviewed mapping, contents and extension casing survive");
            Check(excluded.All(pair => Hash(pair.Key) == pair.Value), mode + ": excluded files remain untouched");
        }

        var changed = Analyze(source);
        string target = changed.Changes[0].File.Path;
        DateTime written = File.GetLastWriteTimeUtc(target);
        using (var stream = new FileStream(target, FileMode.Open, FileAccess.Write))
        { stream.Position = stream.Length - 1; stream.WriteByte(99); }
        File.SetLastWriteTimeUtc(target, written);
        var snapshot = Snapshot(source);
        Check(changed.Changes[0].File.Stamp is not null, "Rename tests have filesystem change stamps");
        Check(Rejected(() => changed.Execute(progress, ct)) && SameSnapshot(source, snapshot),
            "A same-size edit with restored write time invalidates review before any rename");

        var replaced = Analyze(source);
        target = replaced.Changes[0].File.Path;
        string held = target + ".held";
        File.Move(target, held); File.Copy(held, target);
        File.SetCreationTimeUtc(target, replaced.Changes[0].File.Created);
        File.SetLastWriteTimeUtc(target, replaced.Changes[0].File.LastWrite);
        snapshot = Snapshot(source);
        Check(Rejected(() => replaced.Execute(progress, ct)) && SameSnapshot(source, snapshot),
            "An identical replacement with matching size/timestamps invalidates rename review");
        File.Delete(target); File.Move(held, target);

        var occupied = Analyze(source);
        Directory.CreateDirectory(occupied.Changes[0].Destination);
        snapshot = Snapshot(source);
        Check(Rejected(() => occupied.Execute(progress, ct)) && SameSnapshot(source, snapshot),
            "A newly occupied destination blocks execution without changing files");
        Directory.Delete(occupied.Changes[0].Destination);

        var locked = Analyze(source);
        snapshot = Snapshot(source);
        using (var handle = new FileStream(locked.Changes[0].File.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(Rejected(() => locked.Execute(progress, ct)), "An exclusively locked source blocks rename");
        Check(SameSnapshot(source, snapshot), "Locked sources leave every original file untouched");

        foreach (bool duringStaging in new[] { true, false })
        {
            var plan = Analyze(source);
            snapshot = Snapshot(source);
            using var cancellation = new CancellationTokenSource();
            int reports = 0;
            var cancelProgress = new TestProgress
            {
                OnMessage = message =>
                {
                    if (message.StartsWith(duringStaging ? "Staging rename:" : "Renaming reviewed files") && ++reports == 2)
                        cancellation.Cancel();
                }
            };
            bool cancelled = false;
            try { plan.Execute(cancelProgress, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && SameSnapshot(source, snapshot),
                "Cancellation during " + (duringStaging ? "staging" : "final renaming") + " restores all original names and bytes");
        }

        string cycle = Directory.CreateDirectory(Path.Combine(parent, "rename-cycle")).FullName;
        write(Path.Combine(cycle, "0.mp4"), 2, 1); write(Path.Combine(cycle, "10.mp4"), 2, 2);
        var swap = RenamePlan.Analyze(cycle, PresetAction.ShuffleAndName, 0, 0, "", 12,
            MainShell.VideoExtensions, progress, ct, new RotateOrderRandom());
        var cycleHashes = Snapshot(cycle);
        swap.Execute(progress, ct);
        Check(swap.Changes.All(c => Hash(c.Destination) == cycleHashes[c.File.Path]), "Two-pass renaming safely swaps occupied original filenames");
        var unchanged = RenamePlan.Analyze(cycle, PresetAction.NameByTens, 0, 0, "", 12,
            MainShell.VideoExtensions, progress, ct);
        Check(unchanged.Execute(progress, ct).Contains("Renamed: 0\nUnchanged: 2"), "Already numbered clips remain unchanged");

        string aliases = Directory.CreateDirectory(Path.Combine(parent, "rename-hardlinks")).FullName;
        string originalLink = Path.Combine(aliases, "first.mp4");
        write(originalLink, 2, 1);
        if (!CreateHardLink(Path.Combine(aliases, "second.mp4"), originalLink, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var aliasPlan = Analyze(aliases);
        aliasPlan.Execute(progress, ct);
        Check(aliasPlan.Changes.All(c => PhysicalFile.Read(c.Destination) == c.File.Physical),
            "Renaming multiple hard-link aliases preserves their shared file identity");
    }

    private static void DurationOrder(string parent, Action<string, uint, byte> write)
    {
        string folder = Directory.CreateDirectory(Path.Combine(parent, "rename-duration-order")).FullName;
        uint[] durations = [2, 8, 5, 2, 12, 1, 4, 10, 3, 6, 7, 9];
        for (int i = 0; i < durations.Length; i++)
            write(Path.Combine(folder, $"clip-{i:D2}{(i % 2 == 0 ? ".MP4" : ".mp4")}"), durations[i], (byte)(180 + i));
        var original = Snapshot(folder);
        var plan = RenamePlan.Analyze(folder, PresetAction.ShuffleAndName, 100, 4, "clip_", 12,
            MainShell.VideoExtensions, new TestProgress(), CancellationToken.None, new RotateOrderRandom());
        Check(SameSnapshot(folder, original) && plan.Changes.Select(c => c.Duration)
            .SequenceEqual(durations.Order().Select(d => (TimeSpan?)TimeSpan.FromSeconds(d))),
            "Shuffle & Name preview orders clips shortest to longest without changing files");
        Check(plan.Changes.Where(c => c.Duration == TimeSpan.FromSeconds(2)).Select(c => Path.GetFileName(c.File.Path))
            .SequenceEqual(["clip-03.mp4", "clip-00.MP4"]), "Equal-duration clips retain their shuffled order");
        Check(plan.Changes.Select(c => Path.GetFileNameWithoutExtension(c.Destination))
            .SequenceEqual(Enumerable.Range(0, durations.Length).Select(i => $"clip_{100 + 10 * i:D4}")),
            "Duration numbering preserves the starting number, step of ten, padding and prefix above nine clips");
        plan.Execute(new TestProgress(), CancellationToken.None);
        Check(plan.Changes.All(c => Hash(c.Destination) == original[c.File.Path]
            && ClipIdentifier.ReadIdentity(c.Destination, CancellationToken.None).Duration == c.Duration
            && Path.GetExtension(c.File.Path) == Path.GetExtension(c.Destination)),
            "Shuffle & Name executes the exact duration-ordered mapping with original bytes and extensions");

        File.WriteAllText(Path.Combine(folder, "invalid.mp4"), "not a video");
        original = Snapshot(folder);
        bool rejected = false;
        try
        {
            RenamePlan.Analyze(folder, PresetAction.ShuffleAndName, 0, 0, "", 12,
                MainShell.VideoExtensions, new TestProgress(), CancellationToken.None);
        }
        catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or InvalidDataException)
        { rejected = true; }
        Check(rejected && SameSnapshot(folder, original), "Unreadable duration stops Shuffle & Name before any filename changes");
    }

    private static bool Rejected(Action action)
    { try { action(); return false; } catch (IOException) { return true; } }
    private static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static Dictionary<string, string> Snapshot(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
    private static bool SameSnapshot(string folder, Dictionary<string, string> snapshot)
    {
        var current = Snapshot(folder);
        return current.Count == snapshot.Count && snapshot.All(p => current.TryGetValue(p.Key, out string? hash) && hash == p.Value);
    }
    private static ulong ReadBytes()
    {
        using var process = Process.GetCurrentProcess();
        if (!GetProcessIoCounters(process.Handle, out var counters)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return counters.ReadBytes;
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existing, IntPtr security);
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
}
