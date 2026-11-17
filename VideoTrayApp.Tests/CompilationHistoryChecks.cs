using System.Diagnostics;
using System.Security.Cryptography;
using VideoTrayApp;

internal static class CompilationHistoryChecks
{
    internal static void Run(string root, Action<string, uint, byte> write)
    {
        History(root, write);
        Management(root, write);
        Actions(root, write);
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
        Check(check.RecentCount == 0 && check.SavedCompilations == 0 && history.List(CancellationToken.None, clock).Count == 0
            && check.Clips.Single(c => c.Clip.File.Path == renamed).LastUse is null,
            "A record older than 30 days is removed automatically and no longer flags its clips");
        var later = history.Save(compilation, "Second publication", true, progress, CancellationToken.None, clock);
        check = history.Check(candidates, false, progress, CancellationToken.None, clock);
        Check(later.Clips.Count == 3 && check.SavedCompilations == 1 && check.RecentCount == 1
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

    private static void Management(string root, Action<string, uint, byte> write)
    {
        string data = Folder(root, "history-manage-data"), source = Folder(root, "history-manage-clips");
        string clip = Path.Combine(source, "one.mp4"); write(clip, 2, 175);
        var clock = new FixedTime(new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        var now = clock.Now; var history = new CompilationHistory(data); var progress = new TestProgress();
        var ct = CancellationToken.None;
        var past = history.Save(source, "Same name", false, progress, ct, clock, now.AddDays(-10));
        var future = history.Save(source, "Same name", false, progress, ct, clock, now.AddDays(90));
        var check = history.Check(source, false, progress, ct, clock);
        Check(check.RecentCount == 1 && check.Clips.Single().RecentCompilations == 2 && check.Clips.Single().LastUse?.Id == future.Id,
            "Recency uses chosen publish dates and also reserves clips in future compilations");
        var changed = history.ChangePublishedDate(past.Id, now.AddDays(100).ToOffset(TimeSpan.FromHours(8)), ct, clock);
        var reloaded = new CompilationHistory(data).List(ct, clock);
        Check(changed.Id == past.Id && changed.SavedAtUtc == past.SavedAtUtc && changed.Clips.SequenceEqual(past.Clips)
            && reloaded.First().Id == past.Id && reloaded.First().PublishTimeUtc == now.AddDays(100),
            "Rescheduling survives reload and preserves the snapshot ID, original save time and clip hashes");
        history.Delete(past.Id, ct, clock);
        Check(history.List(ct, clock).Single().Id == future.Id && File.Exists(clip),
            "Deletion targets a specific record even with duplicate names and leaves its files intact");
        clock.Now = now.AddDays(60);
        Check(history.Cleanup(ct, clock) == 0 && history.Check(source, false, progress, ct, clock).RecentCount == 1,
            "Future compilations survive cleanup even when their save time is over 30 days old");
        clock.Now = future.PublishTimeUtc.AddDays(30);
        Check(history.Cleanup(ct, clock) == 0 && history.List(ct, clock).Count == 1, "Cleanup keeps the exact 30-day publish cutoff");
        clock.Now = clock.Now.AddTicks(1);
        Check(history.Cleanup(ct, clock) == 1 && new CompilationHistory(data).List(ct, clock).Count == 0 && File.Exists(clip),
            "Automatic expiration persists removal beyond the cutoff and retains the original clip");

        clock.Now = now;
        // Simulate the original version with no publish-date field.
        string legacy = System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = 1, HashAlgorithm = "SHA-256", Compilations = new[] { new
            { past.Id, past.Name, past.Folder, past.SavedAtUtc, past.IncludesSubfolders, past.Clips } }
        });
        File.WriteAllText(history.LogPath, legacy);
        var migrated = history.List(ct, clock).Single();
        Check(migrated.PublishTimeUtc == past.SavedAtUtc && migrated.Id == past.Id && File.ReadAllText(history.LogPath) == legacy,
            "Version 1 logs use their original save time as publication time without rewriting unexpired history");
        history.ChangePublishedDate(past.Id, now.AddDays(2), ct, clock);
        Check(System.Text.Json.JsonDocument.Parse(File.ReadAllText(history.LogPath)).RootElement.GetProperty("Version").GetInt32() == 2
            && new CompilationHistory(data).List(ct, clock).Single().PublishTimeUtc == now.AddDays(2),
            "Editing legacy history writes the version 2 schema atomically");
        string initial = File.ReadAllText(history.LogPath);
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            Expect<OperationCanceledException>(() => history.ChangePublishedDate(past.Id, now, cancel.Token, clock));
            Expect<OperationCanceledException>(() => history.Delete(past.Id, cancel.Token, clock));
        }
        using (var held = new FileStream(history.LogPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Expect<IOException>(() => history.ChangePublishedDate(past.Id, now, ct, clock));
            Expect<IOException>(() => history.Delete(past.Id, ct, clock));
            clock.Now = now.AddDays(40);
            Expect<IOException>(() => history.Cleanup(ct, clock));
        }
        Check(File.ReadAllText(history.LogPath) == initial, "Cancelled and failed date edits, deletions and cleanup preserve the complete previous log");
        clock.Now = now;
        history.ChangePublishedDate(past.Id, now.AddDays(-31), ct, clock);
        Check(history.List(ct, clock).Count == 0, "Moving a publish date beyond retention expires that record immediately");
    }

    private static void Actions(string root, Action<string, uint, byte> write)
    {
        string source = Folder(root, "history-actions-source"), candidate = Folder(root, "history-actions-candidates");
        string destination = Folder(root, "history-actions-target"), data = Folder(root, "history-actions-data");
        string original = Path.Combine(source, "one.mp4"); write(original, 2, 176);
        string first = Path.Combine(candidate, "one.mp4"), second = Path.Combine(Folder(candidate, "nested"), "one.mp4");
        File.Copy(original, first); File.Copy(original, second);
        string unused = Path.Combine(candidate, "unused.mp4"); write(unused, 2, 177);
        var ct = CancellationToken.None; var progress = new TestProgress(); var history = new CompilationHistory(data);
        var saved = history.Save(source, "Reserved", false, progress, ct);
        var scan = history.Check(candidate, true, progress, ct);
        var selection = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first, second };
        Expect<ArgumentException>(() => CompilationRecencyActions.Prepare(history, scan, new HashSet<string> { unused }, null, progress, ct));
        var plan = CompilationRecencyActions.Prepare(history, scan, selection, destination, progress, ct);
        Check(plan.Changes.Select(c => c.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "Recency moves reserve distinct destinations for selected clips with matching filenames");
        int moving = 0;
        var cancelling = new TestProgress { OnMessage = m => { if (m == "Moving one.mp4" && ++moving == 2) throw new IOException("Injected move failure"); } };
        Expect<IOException>(() => plan.Execute(cancelling, ct));
        Check(File.Exists(first) && File.Exists(second) && Directory.GetFiles(destination).Length == 0,
            "A failed recency move restores previously moved files");
        string conflict = plan.Changes.First().Destination;
        File.WriteAllText(conflict, "Keep this existing file");
        Expect<IOException>(() => plan.Execute(progress, ct));
        scan = history.Check(candidate, true, progress, ct);
        plan = CompilationRecencyActions.Prepare(history, scan, selection, destination, progress, ct);
        plan.Execute(progress, ct);
        Check(!File.Exists(first) && !File.Exists(second) && File.Exists(unused) && File.Exists(original)
            && File.ReadAllText(conflict) == "Keep this existing file" && history.List(ct).Single().Id == saved.Id,
            "Recency moves only selected matches, avoids overwrites and preserves saved history and other clips");
        File.Copy(original, first);
        scan = history.Check(candidate, false, progress, ct);
        selection = new(StringComparer.OrdinalIgnoreCase) { first };
        write(first, 2, 178);
        Expect<IOException>(() => CompilationRecencyActions.Prepare(history, scan, selection, null, progress, ct));
        File.Copy(original, first, true); scan = history.Check(candidate, false, progress, ct);
        plan = CompilationRecencyActions.Prepare(history, scan, selection, null, progress, ct);
        write(first, 2, 179);
        Expect<IOException>(() => plan.Execute(progress, ct));
        Check(File.Exists(first), "Clips changed after checking or review cannot be recycled using stale results");
        File.Copy(original, first, true); scan = history.Check(candidate, false, progress, ct);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Expect<OperationCanceledException>(() => CompilationRecencyActions.Prepare(history, scan, selection, null, progress, cancelled.Token));
        history.Delete(saved.Id, ct);
        Expect<IOException>(() => CompilationRecencyActions.Prepare(history, scan, selection, null, progress, ct));
        Check(File.Exists(first), "Cancelled planning or deletion of the matching history entry blocks stale recency actions");
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
                Control.CheckForIllegalCrossThreadCalls = true;
                string data = Folder(root, "history-ui-data"), source = Folder(root, "history-ui-clips");
                string first = Path.Combine(source, "one.mp4"), second = Path.Combine(source, "two.mp4");
                write(first, 2, 191); write(second, 3, 192);
                new UiPreferences { SetupDismissed = true, CloseToTray = false }.Save(data);
                File.WriteAllText(Path.Combine(data, "working_folder.txt"), source);
                var expiredHistory = new CompilationHistory(data);
                var expired = expiredHistory.Save(source, "Expired", false, new TestProgress(), CancellationToken.None);
                AtomicJson.Save(expiredHistory.LogPath, new CompilationHistoryLog
                { Compilations = [expired with { PublishedAtUtc = DateTimeOffset.UtcNow.AddDays(-31) }] });
                using var shell = new MainShell(data); shell.Show(); Application.DoEvents();
                PumpUntil(() => System.Text.Json.JsonDocument.Parse(File.ReadAllText(expiredHistory.LogPath)).RootElement
                    .GetProperty("Compilations").GetArrayLength() == 0);
                Check(File.Exists(first) && File.Exists(second), "Startup automatically removes expired history records without deleting videos");
                shell.OpenCompilationHistory(true, source); Application.DoEvents();
                var save = Descendants(shell).OfType<CompilationHistoryWorkspace>().Single(w => w.Visible);
                Check(Descendants(save).OfType<Button>().Any(b => b.Text == "Save compilation state"), "Save compilation state is reachable under Batches");
                Descendants(save).OfType<TextBox>().Single(t => t.AccessibleName == "Compilation history name").Text = "Published UI compilation";
                DateTime published = DateTime.Today.AddDays(7).AddHours(15);
                Descendants(save).OfType<DateTimePicker>().Single(t => t.AccessibleName == "Compilation publish date (local)").Value = published;
                Wait(save.RunAsync());
                Check(File.Exists(shell.CompilationHistory.LogPath)
                    && Descendants(save).OfType<FileContextList>().Single().Rows.Count == 2,
                    "The save UI records real hashes and displays the saved compilation clips");
                Wait(save.ReloadHistoryAsync());
                Descendants(save).OfType<Button>().Single(b => b.Text == "Saved compilations").PerformClick(); Application.DoEvents();
                var compilationTable = Descendants(save).OfType<DataGridView>().Single(t => t.AccessibleName == "Saved compilations");
                PumpUntil(() => compilationTable.Enabled);
                Check(compilationTable.RowCount == 1 && compilationTable.Rows[0].Cells[0].Value?.ToString() == "Published UI compilation"
                    && (DateTime)compilationTable.Rows[0].Cells[1].Value! == published,
                    "The saved compilations list shows the chosen name and future publish date");
                var updated = published.AddDays(14);
                Descendants(save).OfType<DateTimePicker>().Single(t => t.AccessibleName == "Selected compilation publish date (local)").Value = updated;
                Wait(save.ChangeDateAsync());
                Check((DateTime)compilationTable.Rows[0].Cells[1].Value! == updated
                    && shell.CompilationHistory.List(CancellationToken.None).Single().PublishTimeUtc.LocalDateTime == updated,
                    "Changing the publish date updates both the list and persisted compilation");
                if (Environment.GetEnvironmentVariable("CLIPSMANAGER_SCREENSHOTS") is { Length: > 0 } historyImages)
                {
                    Directory.CreateDirectory(historyImages); Screenshot(shell, Path.Combine(historyImages, "saved-compilations.png"));
                    shell.ClientSize = new Size(700, 520); Application.DoEvents();
                    Screenshot(shell, Path.Combine(historyImages, "saved-compilations-compact.png"));
                    var scroll = (Panel)compilationTable.Parent!;
                    Check(compilationTable.Height >= 220 && scroll.VerticalScroll.Visible, "Compact compilation history retains a scrollable list below its controls");
                    scroll.ScrollControlIntoView(compilationTable); Application.DoEvents();
                    Screenshot(shell, Path.Combine(historyImages, "saved-compilations-compact-scrolled.png"));
                    scroll.AutoScrollPosition = Point.Empty; shell.ClientSize = new Size(1180, 760);
                }
                File.Move(first, Path.Combine(source, "renamed.MP4"));
                write(Path.Combine(source, "new.mp4"), 4, 193);
                shell.OpenCompilationHistory(false, source); Application.DoEvents();
                var check = Descendants(shell).OfType<CompilationHistoryWorkspace>().Single(w => w.Visible);
                Wait(check.RunAsync());
                var list = Descendants(check).OfType<FileContextList>().Single();
                Check(list.Rows.Count == 2 && list.Rows.All(r => r.Action == "SCHEDULED" && r.Proposed == "Published UI compilation" && r.Selectable)
                    && list.Rows.Any(r => r.Path.EndsWith("renamed.MP4")),
                    "The recency UI flags renamed clips reserved for future publication and displays the compilation");
                Descendants(check).OfType<CheckBox>().Single(c => c.Text == "Show only recent clips").Checked = false;
                Check(list.Rows.Count == 3 && list.Rows.Count(r => r.Action == "No saved use") == 1,
                    "The recency view can show all checked clips, including those with no saved use");
                list.SetFilter("SCHEDULED"); Check(list.Table.RowCount == 2, "Scheduled flags remain filterable in the results grid"); list.SetFilter("");
                Button CheckButton(string text) => Descendants(check).OfType<Button>().Single(b => b.Text == text);
                Check(!CheckButton("Move selected…").Enabled && !CheckButton("Delete selected…").Enabled, "Recency file actions require an explicit selection");
                list.SetFilter("renamed.MP4"); list.SelectVisible(); list.SetFilter("two.mp4");
                Check(CheckButton("Move selected…").Enabled && list.SelectedKeys.Count == 1, "Recency selections survive filtering and enable move/delete actions");
                void Act(string? target, DialogResult response)
                {
                    bool reviewed = false;
                    using var confirm = new System.Windows.Forms.Timer { Interval = 10 };
                    confirm.Tick += (_, _) =>
                    {
                        if (Application.OpenForms.OfType<ReviewConfirmation>().FirstOrDefault() is not { } dialog) return;
                        reviewed = true; dialog.DialogResult = response;
                    };
                    confirm.Start(); Wait(check.ActAsync(target)); confirm.Stop();
                    Check(reviewed, "Recency actions present a review before changing files");
                }
                Act(null, DialogResult.Cancel);
                Check(File.Exists(Path.Combine(source, "renamed.MP4")) && list.SelectedKeys.Count == 1,
                    "Cancelling recency deletion retains the clip and hidden selection");
                string movedTo = Folder(root, "history-ui-moved");
                Act(movedTo, DialogResult.OK);
                Check(File.Exists(Path.Combine(movedTo, "renamed.MP4")) && !File.Exists(Path.Combine(source, "renamed.MP4"))
                    && File.Exists(second) && list.Rows.Count == 2, "Confirmed recency Move changes only selected matches and updates the results");
                list.SetFilter(""); list.SelectVisible();
                Act(null, DialogResult.OK);
                Check(!File.Exists(second) && File.Exists(Path.Combine(source, "new.mp4")) && list.Rows.Count == 1
                    && shell.CompilationHistory.List(CancellationToken.None).Count == 1 && !CheckButton("Delete selected…").Enabled,
                    "Confirmed recency Delete recycles selected matches and preserves unflagged clips and publication history");
                Wait(check.RunAsync());
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
