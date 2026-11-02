using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using VideoTrayApp;

internal static class DurationCompilationChecks
{
    internal static void Run(string root, Action<string, uint, byte> write)
    {
        Duration(root, write);
        Compilation(root, write);
        Ui(root, write);
    }

    private static void Duration(string root, Action<string, uint, byte> write)
    {
        var exact = DurationQuery.Parse("01:02:15", 30, DurationMatchMode.Exact);
        Check(exact.Matches(TimeSpan.FromSeconds(62.5)) && !exact.Matches(TimeSpan.FromSeconds(62.54)),
            "MM:SS:FF exact search compares durations at the selected frame rate");
        Check(DurationQuery.Parse("00:01:15", 60, DurationMatchMode.Exact).Matches(TimeSpan.FromSeconds(1.25))
            && DurationQuery.Parse("00:01:15", 30, DurationMatchMode.Exact).Matches(TimeSpan.FromSeconds(1.5))
            && DurationQuery.Parse("01:00:00", 29.97m, DurationMatchMode.Exact).Matches(TimeSpan.FromMinutes(1)),
            "Frame rate changes the frame field and fractional frame rates are supported");
        foreach (string invalid in new[] { "00:00:00", "00:60:00", "00:01:30", "-1:02:00", "00:02", "00:02:00:00", "letters", "999999999999999999:00:00" })
        {
            bool rejected = false;
            try { DurationQuery.Parse(invalid, 30, DurationMatchMode.Exact); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid timecode is rejected: " + invalid);
        }
        var around = DurationQuery.Parse("00:10:00", 30, DurationMatchMode.WithinFiveSeconds);
        var shorter = around with { Mode = DurationMatchMode.UpToFiveSecondsShorter };
        var longer = around with { Mode = DurationMatchMode.UpToFiveSecondsLonger };
        Check(around.Matches(TimeSpan.FromSeconds(5)) && around.Matches(TimeSpan.FromSeconds(15))
            && !around.Matches(TimeSpan.FromSeconds(4.9)) && !around.Matches(TimeSpan.FromSeconds(15.1))
            && shorter.Matches(TimeSpan.FromSeconds(5)) && !shorter.Matches(TimeSpan.FromSeconds(11))
            && longer.Matches(TimeSpan.FromSeconds(15)) && !longer.Matches(TimeSpan.FromSeconds(9)),
            "Five-second shorter, longer and either-side ranges include endpoints and exclude out-of-range clips");
        string source = Folder(root, "duration-source"), nested = Folder(source, "nested");
        string first = Path.Combine(source, "first.mp4"), second = Path.Combine(nested, "second.MP4");
        write(first, 10, 101); write(second, 10, 102); write(Path.Combine(source, "outside.mp4"), 16, 103);
        File.WriteAllText(Path.Combine(source, "invalid.mp4"), "invalid MP4");
        File.Copy(first, Path.Combine(source, "ignored.mov"));
        var query = DurationQuery.Parse("00:10:00", 30, DurationMatchMode.Exact);
        var found = ClipDurationLookup.Find(source, query, true, new TestProgress(), CancellationToken.None, random: new Random(3));
        Check(found.Clip is not null && found.Matches == 2 && found.Scanned == 4 && found.Errors.Count == 1,
            "Recursive duration search returns one random match and distinguishes invalid and non-MP4 files");
        var next = ClipDurationLookup.Find(source, query, true, new TestProgress(), CancellationToken.None, found.Clip!.Path);
        Check(next.Clip is not null && next.Clip.Path != found.Clip.Path && next.Matches == 2,
            "Finding another random clip avoids immediately repeating the previous match when alternatives exist");
        var direct = ClipDurationLookup.Find(source, query, false, new TestProgress(), CancellationToken.None, first);
        Check(direct.Matches == 1 && direct.Clip?.Path == first, "Direct-folder search and retrying a sole match work");
        var none = ClipDurationLookup.Find(source, query with { Frames = 3000 }, true, new TestProgress(), CancellationToken.None);
        Check(none.Matches == 0 && none.Clip is null, "No matching duration yields no random result");
        string frameClip = Path.Combine(source, "frame.mp4"); write(frameClip, 1, 104);
        var frameBytes = File.ReadAllBytes(frameClip); BinaryPrimitives.WriteUInt32BigEndian(frameBytes.AsSpan(56), 1033); File.WriteAllBytes(frameClip, frameBytes);
        var frame = ClipDurationLookup.Find(source, DurationQuery.Parse("00:01:01", 30, DurationMatchMode.Exact), false, new TestProgress(), CancellationToken.None);
        Check(frame.Clip?.Path == frameClip && frameBytes.SequenceEqual(File.ReadAllBytes(frameClip)),
            "Fractional-second MP4 metadata matches the entered frame and lookup preserves original bytes");
        Check(DurationQuery.Parse("00:01:01", 30, DurationMatchMode.Exact).Format(frame.Clip!.Duration) == "00:01:01",
            "The displayed frame duration agrees with nearest-frame exact matching");
        using var cancel = new CancellationTokenSource(); bool cancelled = false;
        try { ClipDurationLookup.Find(source, query, true, new TestProgress { OnMessage = _ => cancel.Cancel() }, cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "Duration scans support cancellation");
    }

    private static void Compilation(string root, Action<string, uint, byte> write)
    {
        string data = Folder(root, "compilation-settings"), source = Folder(root, "compilation-source");
        string output = Folder(root, "compilation-output"), other = Folder(root, "compilation-other");
        string first = Path.Combine(source, "same.mp4"), second = Path.Combine(Folder(source, "nested"), "same.mp4");
        write(first, 2, 121); write(second, 3, 122);
        string existing = Path.Combine(output, "0001.MP4"); write(existing, 4, 123);
        string existingHash = Hash(existing);
        File.WriteAllText(Path.Combine(output, "notes.txt"), "keep notes"); Folder(output, "0002.mp4");
        string nested = Path.Combine(Folder(output, "nested"), "untouched.mp4"); write(nested, 90, 124);
        var originals = Snapshot(source); var store = new CompilationSessions(data);
        var session = store.LockIn(output); store.WatchSource(session.Id, source);
        var another = store.LockIn(other); store.Select(session.Id);
        var loaded = new CompilationSessions(data);
        Check(loaded.Sessions.Count == 2 && loaded.Active?.Id == session.Id && loaded.Active.SourceFolder == source,
            "Multiple compilation sessions, their watched sources and active selection survive reload");
        bool overlap = false;
        try { store.LockIn(Path.Combine(output, "nested")); } catch (IOException) { overlap = true; }
        bool sourceOverlap = false;
        try { store.WatchSource(session.Id, output); } catch (IOException) { sourceOverlap = true; }
        Check(overlap && sourceOverlap, "Overlapping compilation sessions and watched source/destination paths are rejected");
        string summary = CompilationPreparer.Add(session, [first, second], new TestProgress(), CancellationToken.None, new RotateOrderRandom());
        Check(summary.Contains("Added and verified: 2") && Snapshot(source).SequenceEqual(originals)
            && Hashes(output).Order().SequenceEqual(new[] { Hash(first), Hash(second), existingHash }.Order()),
            "Compilation additions leave original source paths and bytes unchanged");
        Check(Directory.GetFiles(output).Count(VideoFiles.IsMp4) == 3 && Hashes(output).Contains(Hash(first)) && Hashes(output).Contains(Hash(second))
            && File.ReadAllText(Path.Combine(output, "notes.txt")) == "keep notes" && Directory.Exists(Path.Combine(output, "0002.mp4"))
            && File.Exists(nested), "Verified copies with identical source filenames coexist; notes, subfolders and occupied numbers are preserved");
        var catalog = new CompilationCatalog(); var listed = catalog.Read(output, CancellationToken.None);
        Check(listed.Clips.Count == 3 && listed.TotalDuration == TimeSpan.FromSeconds(9) && listed.Errors.Count == 0,
            "Compilation total duration includes every direct clip and excludes subfolders and non-video files");
        var before = Snapshot(output);
        foreach (string stage in new[] { "Verified compilation clip:", "Renaming reviewed files" })
        {
            using var cancel = new CancellationTokenSource(); bool cancelled = false;
            try { CompilationPreparer.Add(session, [first, second], new TestProgress { OnMessage = m => { if (m.StartsWith(stage)) cancel.Cancel(); } }, cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && Snapshot(output).SequenceEqual(before), "Cancellation during " + stage + " restores the entire prior compilation and cleans temporary copies");
        }
        string invalid = Path.Combine(source, "invalid.mp4"); File.WriteAllText(invalid, "bad video");
        bool failed = false;
        try { CompilationPreparer.Add(session, [first, invalid], new TestProgress(), CancellationToken.None); } catch (IOException) { failed = true; }
        Check(failed && Snapshot(output).SequenceEqual(before), "A later invalid clip rolls back earlier new copies without changing the previous compilation");
        var namesBefore = Directory.GetFiles(output).Where(VideoFiles.IsMp4).Order().ToDictionary(p => Path.GetFileName(p), Hash);
        CompilationPreparer.Add(session, [first], new TestProgress(), CancellationToken.None, new RotateOrderRandom());
        var afterAdd = catalog.Read(output, CancellationToken.None);
        Check(afterAdd.Clips.Count == 4 && afterAdd.TotalDuration == TimeSpan.FromSeconds(11)
            && namesBefore.Any(p => !File.Exists(Path.Combine(output, p.Key)) || Hash(Path.Combine(output, p.Key)) != p.Value),
            "Each new add reshuffles existing clips and updates the total duration");
        string damaged = Path.Combine(output, "damaged.mp4"); File.WriteAllText(damaged, "unreadable");
        var partial = catalog.Read(output, CancellationToken.None);
        Check(partial.Errors.Count == 1 && partial.Clips.Count == 5 && partial.TotalDuration == TimeSpan.FromSeconds(11),
            "Unreadable compilation durations produce a partial total with explicit errors");
        File.Delete(damaged);
        using (var heldSettings = new FileStream(Path.Combine(data, "compilation_sessions.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool saveFailed = false;
            try { store.Rename(session.Id, "cannot-save", CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { saveFailed = true; }
            Check(saveFailed && Directory.Exists(output) && !Directory.Exists(Path.Combine(root, "cannot-save")) && store.Active?.Folder == output,
                "A failed settings save restores the original compilation folder name and active session");
        }
        var renamed = store.Rename(session.Id, "renamed-compilation", CancellationToken.None);
        Check(!Directory.Exists(output) && Directory.Exists(renamed.Folder) && new CompilationSessions(data).Active?.Folder == renamed.Folder
            && store.Active?.SourceFolder == source && Directory.Exists(other), "Renaming a compilation updates its real directory and saved session while retaining its watched source");
        bool occupied = false;
        try { store.Rename(session.Id, Path.GetFileName(other), CancellationToken.None); } catch (IOException) { occupied = true; }
        Check(occupied && Directory.Exists(renamed.Folder), "Compilation folder rename never overwrites an occupied destination");
        store.Forget(session.Id);
        Check(store.Sessions.Count == 1 && store.Active?.Id == another.Id && Directory.Exists(renamed.Folder),
            "Forgetting a session retains its clips and selects another saved session");
        string brokenData = Folder(root, "compilation-broken-settings");
        File.WriteAllText(Path.Combine(brokenData, "compilation_sessions.json"), "broken JSON");
        var broken = new CompilationSessions(brokenData); bool blocked = false;
        try { broken.LockIn(renamed.Folder); } catch (IOException) { blocked = true; }
        Check(blocked && File.ReadAllText(Path.Combine(brokenData, "compilation_sessions.json")) == "broken JSON",
            "Corrupt session settings remain available for recovery and are never silently overwritten");
    }

    private static void Ui(string root, Action<string, uint, byte> write)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                Control.CheckForIllegalCrossThreadCalls = true;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                string data = Folder(root, "duration-compilation-ui-data"), source = Folder(root, "duration-compilation-ui-source");
                string output = Folder(root, "duration-compilation-ui-output"), secondOutput = Folder(root, "duration-compilation-ui-second");
                new UiPreferences { SetupDismissed = true, CloseToTray = false }.Save(data);
                File.WriteAllText(Path.Combine(data, "working_folder.txt"), source);
                string first = Path.Combine(source, "one.mp4"); write(first, 2, 131);
                write(Path.Combine(source, "two.mp4"), 3, 132);
                using var shell = new MainShell(data); shell.Show(); Application.DoEvents();
                shell.Navigate("Batches"); Button(shell, "Preparing compilation").PerformClick(); Application.DoEvents();
                var workspace = Descendants(shell).OfType<CompilationWorkspace>().Single();
                Descendants(workspace).OfType<PathPicker>().Single(p => Descendants(p).Any(c => c.AccessibleName == "Compilation folder")).Value = output;
                Button(workspace, "Lock in folder").PerformClick(); Application.DoEvents();
                Check(shell.Compilations.Active?.Folder == output && !Button(workspace, "Lock in folder").Enabled,
                    "Preparing compilation is reachable under Batches and locks the chosen destination");
                Descendants(workspace).OfType<PathPicker>().Single(p => Descendants(p).Any(c => c.AccessibleName?.StartsWith("Watched source folder") == true)).Value = source;
                Button(workspace, "Watch this folder").PerformClick();
                PumpUntil(() => Descendants(workspace).OfType<FileContextList>().Single(l => l.Table.AccessibleName == "Watched compilation source clips").Rows.Count == 2);
                var sourceList = Descendants(workspace).OfType<FileContextList>().Single(l => l.Table.AccessibleName == "Watched compilation source clips");
                Check(sourceList.Visible && sourceList.Rows.All(r => r.Selectable) && sourceList.Rows.All(r => r.Duration.Length > 0),
                    "Watched source clips appear in the UI with durations and explicit selections");
                sourceList.SetFilter("one.mp4"); sourceList.SelectVisible();
                string arriving = Path.Combine(source, "arrival.mp4"); write(arriving, 4, 133);
                PumpUntil(() => sourceList.Rows.Count == 3);
                Check(sourceList.SelectedKeys.Contains(first), "Live source arrivals refresh the list while retaining unchanged clip selections");
                string renamedArrival = Path.Combine(source, "renamed-arrival.MP4"); File.Move(arriving, renamedArrival); arriving = renamedArrival;
                PumpUntil(() => sourceList.Rows.Any(r => r.Path == renamedArrival));
                write(renamedArrival, 6, 133);
                PumpUntil(() => sourceList.Rows.Single(r => r.Path == renamedArrival).Duration == "00:00:06");
                Check(sourceList.SelectedKeys.Contains(first), "Watched renames and duration edits refresh metadata without losing other selections");
                sourceList.SetFilter(""); Wait(workspace.AddSelectedAsync());
                Check(File.Exists(first) && Directory.GetFiles(output).Count(VideoFiles.IsMp4) == 1 && Total(workspace).Contains("00:00:02"),
                    "Adding selected watched clips copies exactly those selections and displays the compilation total");
                string external = Path.Combine(output, "external.mp4"); write(external, 5, 134);
                PumpUntil(() => Total(workspace).Contains("00:00:07"));
                Check(Total(workspace).Contains("2 clips"), "External changes to the compilation folder update its total duration automatically");
                shell.Navigate("Find & clean"); Button(shell, "Find by duration").PerformClick(); Application.DoEvents();
                var duration = Descendants(shell).OfType<ClipDurationWorkspace>().Single();
                Descendants(duration).OfType<TextBox>().Single(t => t.AccessibleName == "Duration MM:SS:FF").Text = "00:02:00";
                Wait(duration.FindAsync());
                var match = Descendants(duration).OfType<FileContextList>().Single();
                Check(match.Rows.Count == 1 && match.Rows[0].Path == first && match.Visible,
                    "Find by duration is reachable and returns exactly one native random result");
                Button(match, "Add to compilation").PerformClick();
                PumpUntil(() => Directory.GetFiles(output).Count(VideoFiles.IsMp4) == 3 && !shell.IsBusy);
                Check(File.Exists(first), "Duration results can be added directly to the active compilation without moving sources");
                shell.Navigate("Batches"); Button(shell, "Preparing compilation").PerformClick();
                PumpUntil(() => Total(workspace).Contains("00:00:09"));
                var other = shell.Compilations.LockIn(secondOutput); shell.Compilations.WatchSource(other.Id, source);
                PumpUntil(() => Total(workspace).Contains("00:00:00"));
                Wait(workspace.AddAsync([arriving]));
                Check(Total(workspace).Contains("00:00:06") && Directory.GetFiles(output).Count(VideoFiles.IsMp4) == 3,
                    "Switching compilation sessions isolates additions and total durations");
                Button(workspace, "Session settings").PerformClick();
                Descendants(workspace).OfType<TextBox>().Single(t => t.AccessibleName == "Compilation folder name").Text = "ui-renamed";
                Wait(workspace.RenameAsync());
                Check(shell.Compilations.Active?.Name == "ui-renamed" && Directory.Exists(Path.Combine(root, "ui-renamed")),
                    "Native folder rename persists and rebinds the active compilation");
                PumpUntil(() => Total(workspace).Contains("00:00:06") && sourceList.Rows.Count == 3);
                File.Delete(arriving); PumpUntil(() => sourceList.Rows.Count == 2);
                Check(sourceList.Rows.All(r => r.Path != arriving), "Watched deletions remove stale source rows automatically");
                foreach (var size in new[] { new Size(1180, 760), new Size(640, 480) })
                {
                    shell.ClientSize = size; Application.DoEvents(); Button(workspace, "Watched clips").PerformClick(); Application.DoEvents();
                    Check(workspace.Visible && sourceList.Table.Width > 200 && Button(workspace, "Add selected clips").Visible,
                        $"Compilation controls and clip list remain reachable at {size.Width}x{size.Height}");
                }
                if (Environment.GetEnvironmentVariable("CLIP_COMPILATION_SCREENSHOTS") is { Length: > 0 } screenshots)
                {
                    shell.ClientSize = new Size(1180, 760); Application.DoEvents();
                    Screenshot(shell, Path.Combine(screenshots, "compilation-watched.png"));
                    Button(workspace, "Compilation").PerformClick(); Application.DoEvents(); Screenshot(shell, Path.Combine(screenshots, "compilation-contents.png"));
                    Button(workspace, "Session settings").PerformClick(); Application.DoEvents(); Screenshot(shell, Path.Combine(screenshots, "compilation-settings.png"));
                    shell.Navigate("Find & clean"); Button(shell, "Find by duration").PerformClick(); Application.DoEvents(); Screenshot(shell, Path.Combine(screenshots, "find-duration.png"));
                    Button(duration, "Search settings").PerformClick(); Application.DoEvents(); Screenshot(shell, Path.Combine(screenshots, "find-duration-settings.png"));
                }
                shell.Close(); PumpUntil(() => shell.IsDisposed);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw new Exception("Duration / compilation UI checks failed.", error);
    }

    private static string Total(Control root) => Descendants(root).OfType<Label>().Single(l => l.AccessibleName == "Compilation total duration").Text;
    private static Button Button(Control root, string text) => Descendants(root).OfType<Button>().Single(b => b.Text == text);
    private static void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); Application.DoEvents(); }
    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new Exception("UI check timed out."); Application.DoEvents(); Thread.Sleep(5); }
        Application.DoEvents();
    }
    private static void Screenshot(Form form, string path)
    { using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path); }
    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static string Folder(string root, string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string[] Hashes(string folder) => Directory.GetFiles(folder).Where(VideoFiles.IsMp4).Select(Hash).ToArray();
    private static string[] Snapshot(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order()
        .Select(p => Path.GetRelativePath(folder, p) + ":" + Hash(p)).ToArray();
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
}
