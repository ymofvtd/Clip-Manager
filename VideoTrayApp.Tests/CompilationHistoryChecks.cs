using System.Diagnostics;
using System.Security.Cryptography;
using VideoTrayApp;

internal static class CompilationHistoryChecks
{
    internal static void Run(string root, Action<string, uint, byte> write)
    {
        History(root, write);
        Failures(root, write);
        Ui(root, write);
    }

    private static void History(string root, Action<string, uint, byte> write)
    {
        string data = Folder(root, "history-data"), compilation = Folder(root, "history-compilation");
        string candidates = Folder(root, "history-candidates"), nested = Folder(compilation, "nested");
        var clock = new FixedTime(new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        var history = new CompilationHistory(data);
        string original = Path.Combine(compilation, "first.mp4"); write(original, 2, 171);
        string duplicate = Path.Combine(compilation, "second.MP4"); File.Copy(original, duplicate);
        write(Path.Combine(nested, "nested.mp4"), 3, 172);
        File.WriteAllText(Path.Combine(compilation, "notes.txt"), "not a video");
        string renamed = Path.Combine(candidates, "renamed.MP4"); File.Copy(original, renamed);
        string sameName = Path.Combine(candidates, "first.mp4"); write(sameName, 2, 173);
        var progress = new TestProgress();
        var empty = history.Check(candidates, false, progress, CancellationToken.None, clock);
        Check(empty.SavedCompilations == 0 && empty.RecentCount == 0 && !File.Exists(history.LogPath),
            "Checking without history reports no saved compilations and creates no log");
        var before = Snapshot(compilation);
        var saved = history.Save(compilation, "Published compilation", false, progress, CancellationToken.None, clock);
        Check(saved.Clips.Count == 2 && saved.Clips.Select(c => c.Hash).Distinct().Count() == 1
            && saved.SavedAtUtc == clock.GetUtcNow() && saved.Folder == compilation
            && saved.Clips.All(c => c.Hash == Hash(original)) && Snapshot(compilation).SequenceEqual(before),
            "Save records full SHA-256 hashes, names and save time without changing clips, excluding subfolders by default");
        string logBefore = File.ReadAllText(history.LogPath);
        var check = new CompilationHistory(data).Check(candidates, false, progress, CancellationToken.None, clock);
        Check(check.RecentCount == 1 && check.Clips.Single(c => c.Clip.File.Path == renamed) is { RecentCompilations: 1, LastUse.Name: "Published compilation" }
            && check.Clips.Single(c => c.Clip.File.Path == sameName).LastUse is null,
            "History survives reload, flags renamed copies and counts duplicate copies once per compilation; different content stays unflagged");
        Check(File.ReadAllText(history.LogPath) == logBefore, "Recency checking never updates the log or refreshes saved usage dates");
        clock.Now = saved.SavedAtUtc.AddDays(30);
        Check(history.Check(candidates, false, progress, CancellationToken.None, clock).RecentCount == 1,
            "A clip saved exactly 30 days ago is still flagged (inclusive cutoff)");
        clock.Now = clock.Now.AddTicks(1);
        check = history.Check(candidates, false, progress, CancellationToken.None, clock);
        Check(check.RecentCount == 0 && check.Clips.Single(c => c.Clip.File.Path == renamed).LastUse?.Id == saved.Id,
            "A clip older than 30 days is no longer flagged while its last compilation remains available");
        var later = history.Save(compilation, "Second publication", true, progress, CancellationToken.None, clock);
        check = history.Check(candidates, false, progress, CancellationToken.None, clock);
        Check(later.Clips.Count == 3 && check.SavedCompilations == 2 && check.RecentCount == 1
            && check.Clips.Single(c => c.Clip.File.Path == renamed).LastUse?.Id == later.Id,
            "New saves append history, include optional subfolders and use the most recent compilation");
        var third = history.Save(compilation, "", false, progress, CancellationToken.None, clock);
        var thirdUse = history.Check(candidates, false, progress, CancellationToken.None, clock).Clips.Single(c => c.Clip.File.Path == renamed);
        Check(third.Name == new DirectoryInfo(compilation).Name && thirdUse.RecentCompilations == 2 && thirdUse.LastUse?.Id == third.Id,
            "Empty labels use the folder name and independent recent saves are counted");
        string candidateNested = Folder(candidates, "nested"); File.Copy(original, Path.Combine(candidateNested, "nested-copy.mp4"));
        Check(history.Check(candidates, true, progress, CancellationToken.None, clock).RecentCount == 2
            && history.Check(candidates, false, progress, CancellationToken.None, clock).RecentCount == 1,
            "Recency checking honors optional recursion independently of save options");
        Directory.Move(compilation, compilation + "-moved");
        Check(history.Check(candidates, false, progress, CancellationToken.None, clock).RecentCount == 1,
            "Moving or removing a saved compilation folder does not erase its historical use");
    }

    private static void Failures(string root, Action<string, uint, byte> write)
    {
        string data = Folder(root, "history-failures-data"), source = Folder(root, "history-failures-clips");
        string first = Path.Combine(source, "a.mp4"), second = Path.Combine(source, "b.mp4");
        write(first, 2, 181); write(second, 3, 182);
        var history = new CompilationHistory(data); var progress = new TestProgress();
        history.Save(source, "Initial", false, progress, CancellationToken.None);
        string initial = File.ReadAllText(history.LogPath);
        using (var cancel = new CancellationTokenSource())
        {
            var cancels = new TestProgress { OnMessage = m => { if (m.StartsWith("Hashed clip:")) cancel.Cancel(); } };
            Expect<OperationCanceledException>(() => history.Save(source, "Cancelled", false, cancels, cancel.Token));
            Check(File.ReadAllText(history.LogPath) == initial, "Cancellation during hashing leaves the entire prior history intact");
        }
        using (var held = new FileStream(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Expect<IOException>(() => history.Save(source, "Partial", false, progress, CancellationToken.None));
            var partial = history.Check(source, false, progress, CancellationToken.None);
            Check(partial.Errors.Count == 1 && partial.Clips.Count == 1 && partial.RecentCount == 1 && File.ReadAllText(history.LogPath) == initial,
                "Unreadable clips block incomplete saves; recency checks report partial results and explicit errors");
        }
        var changes = new TestProgress { OnMessage = m => { if (m.StartsWith("Verifying compilation contents")) write(first, 2, 183); } };
        Expect<IOException>(() => history.Save(source, "Changed", false, changes, CancellationToken.None));
        Check(File.ReadAllText(history.LogPath) == initial, "A clip changed after hashing prevents saving stale identities");
        var arrives = new TestProgress { OnMessage = m => { if (m.StartsWith("Verifying compilation contents")) write(Path.Combine(source, "arrival.mp4"), 4, 184); } };
        Expect<IOException>(() => history.Save(source, "Arrival", false, arrives, CancellationToken.None));
        Check(File.ReadAllText(history.LogPath) == initial, "Folder changes during hashing prevent an incomplete saved snapshot");
        using (var held = new FileStream(history.LogPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Expect<IOException>(() => history.Save(source, "Cannot publish", false, progress, CancellationToken.None));
        Check(File.ReadAllText(history.LogPath) == initial && !Directory.EnumerateFiles(data, "*.tmp").Any(),
            "Failed log replacement preserves prior history and cleans its temporary file");
        string empty = Folder(root, "history-empty");
        Expect<IOException>(() => history.Save(empty, "Empty", false, progress, CancellationToken.None));
        Check(File.ReadAllText(history.LogPath) == initial, "Empty folders cannot create misleading saved compilations");
        foreach (string corrupt in new[] { "broken JSON", "{}", "null", "{\"Version\":99,\"HashAlgorithm\":\"SHA-256\",\"Compilations\":[]}" })
        {
            File.WriteAllText(history.LogPath, corrupt);
            Expect<IOException>(() => history.Save(source, "Cannot overwrite", false, progress, CancellationToken.None));
            Expect<IOException>(() => history.Check(source, false, progress, CancellationToken.None));
            Check(File.ReadAllText(history.LogPath) == corrupt, "Corrupt or unsupported history blocks both actions and is preserved for recovery");
        }
        File.WriteAllText(history.LogPath, initial);
        Check(history.Check(source, false, progress, CancellationToken.None).SavedCompilations == 1,
            "Restoring the history file allows checking again without restarting the service");
    }

    private static void Ui(string root, Action<string, uint, byte> write)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                string data = Folder(root, "history-ui-data"), source = Folder(root, "history-ui-clips");
                string first = Path.Combine(source, "one.mp4"), second = Path.Combine(source, "two.mp4");
                write(first, 2, 191); write(second, 3, 192);
                new UiPreferences { SetupDismissed = true, CloseToTray = false }.Save(data);
                File.WriteAllText(Path.Combine(data, "working_folder.txt"), source);
                using var shell = new MainShell(data); shell.Show(); Application.DoEvents();
                shell.OpenCompilationHistory(true, source); Application.DoEvents();
                var save = Descendants(shell).OfType<CompilationHistoryWorkspace>().Single(w => w.Visible);
                Check(Descendants(save).OfType<Button>().Any(b => b.Text == "Save compilation state"), "Save compilation state is reachable under Batches");
                Descendants(save).OfType<TextBox>().Single(t => t.AccessibleName == "Compilation history name").Text = "Published UI compilation";
                Wait(save.RunAsync());
                Check(File.Exists(shell.CompilationHistory.LogPath)
                    && Descendants(save).OfType<FileContextList>().Single().Rows.Count == 2,
                    "The save UI records real hashes and displays the saved compilation clips");
                File.Move(first, Path.Combine(source, "renamed.MP4"));
                write(Path.Combine(source, "new.mp4"), 4, 193);
                shell.OpenCompilationHistory(false, source); Application.DoEvents();
                var check = Descendants(shell).OfType<CompilationHistoryWorkspace>().Single(w => w.Visible);
                Wait(check.RunAsync());
                var list = Descendants(check).OfType<FileContextList>().Single();
                Check(list.Rows.Count == 2 && list.Rows.All(r => r.Action == "RECENT" && r.Proposed == "Published UI compilation")
                    && list.Rows.Any(r => r.Path.EndsWith("renamed.MP4")),
                    "The recency UI flags renamed clips and shows their last compilation and save time");
                Descendants(check).OfType<CheckBox>().Single(c => c.Text == "Show only recent clips").Checked = false;
                Check(list.Rows.Count == 3 && list.Rows.Count(r => r.Action == "No saved use") == 1,
                    "The recency view can show all checked clips, including those with no saved use");
                list.SetFilter("RECENT"); Check(list.Table.RowCount == 2, "Recent flags remain filterable in the results grid"); list.SetFilter("");
                string? screenshots = Environment.GetEnvironmentVariable("CLIPSMANAGER_SCREENSHOTS");
                if (!string.IsNullOrEmpty(screenshots))
                {
                    Directory.CreateDirectory(screenshots);
                    Screenshot(shell, Path.Combine(screenshots, "clip-recency.png"));
                    shell.ClientSize = new Size(700, 520); Application.DoEvents();
                    Screenshot(shell, Path.Combine(screenshots, "clip-recency-compact.png"));
                    shell.ClientSize = new Size(1180, 760); shell.OpenCompilationHistory(true, source); Application.DoEvents();
                    Wait(save.RunAsync()); Screenshot(shell, Path.Combine(screenshots, "save-compilation-state.png"));
                    Descendants(save).OfType<Button>().Single(b => b.Text == "Settings").PerformClick(); Application.DoEvents();
                    Screenshot(shell, Path.Combine(screenshots, "save-compilation-settings.png"));
                }
                shell.Navigate("Batches");
                var tabs = Descendants(shell).OfType<ToolGroupWorkspace>().Single(t => t.AccessibleName == "Batches tools");
                tabs.Select("PreparingCompilation"); Application.DoEvents();
                var compilation = Descendants(shell).OfType<CompilationWorkspace>().Single();
                var store = shell.Compilations; store.LockIn(source);
                Descendants(compilation).OfType<Button>().Single(b => b.Text == "Compilation").PerformClick(); Application.DoEvents();
                Descendants(compilation).OfType<Button>().Single(b => b.Text == "Save compilation state").PerformClick(); Application.DoEvents();
                Check(save.Visible && Descendants(save).OfType<PathPicker>().Single().Value == source,
                    "The active compilation shortcut opens Save compilation state with the correct folder");
                tabs.Select("PreparingCompilation"); Application.DoEvents();
                Descendants(compilation).OfType<Button>().Single(b => b.Text == "Check clips recency").PerformClick(); Application.DoEvents();
                Check(check.Visible && Descendants(check).OfType<PathPicker>().Single().Value == source,
                    "The active compilation shortcut opens Check clips recency with the correct folder");
                using (var releaseJob = new ManualResetEventSlim())
                {
                    var pending = shell.RunJobAsync("History UI busy check", "Test foreground coordination", true,
                        (_, ct) => { releaseJob.Wait(ct); return JobOutcome.Complete("Finished"); });
                    try
                    {
                        shell.OpenCompilationHistory(true, source); Application.DoEvents();
                        Check(!Descendants(save).OfType<Button>().Single(b => b.Text == "Save compilation state").Enabled,
                            "Opening the history tools during another operation disables their run controls");
                    }
                    finally { releaseJob.Set(); }
                    Wait(pending);
                    Check(Descendants(save).OfType<Button>().Single(b => b.Text == "Save compilation state").Enabled,
                        "History controls re-enable when another foreground operation completes");
                }
                shell.Close(); PumpUntil(() => shell.IsDisposed);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw new Exception("Compilation history UI checks failed.", error);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    { internal DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); Application.DoEvents(); }
    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done()) { if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new Exception("History UI check timed out."); Application.DoEvents(); Thread.Sleep(5); }
        Application.DoEvents();
    }
    private static void Screenshot(Form form, string path)
    { using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path); }
    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static string Folder(string root, string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string[] Snapshot(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(folder, p) + ":" + Hash(p)).Order().ToArray();
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAILED: " + message); Console.WriteLine("PASS: " + message); }
}
