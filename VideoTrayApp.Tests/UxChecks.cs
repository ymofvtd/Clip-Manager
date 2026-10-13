using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography;
using VideoTrayApp;

internal static class UxChecks
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr handle, IntPtr device, uint flags);
    internal static void Run(string parent, Action<string, uint, byte> write)
    {
        string source = Directory.CreateDirectory(Path.Combine(parent, "ux-source")).FullName;
        string data = Directory.CreateDirectory(Path.Combine(parent, "ux-data")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(parent, "ux-destination")).FullName;
        var progress = new TestProgress();
        write(Path.Combine(source, "original-a.avi"), 2, 10); write(Path.Combine(source, "original-b.avi"), 3, 20);
        var original = Snapshot(source);
        string scannedPath = Path.Combine(source, "original-a.avi");
        var scannedIdentity = ClipIdentifier.ReadIdentity(scannedPath, CancellationToken.None);
        string matchingReplacement = scannedPath + ".replacement"; File.Copy(scannedPath, matchingReplacement);
        var scannedGuard = PlannedFile.ReadExpected(scannedPath, scannedIdentity, CancellationToken.None);
        File.Move(scannedPath, scannedPath + ".original"); File.Move(matchingReplacement, scannedPath);
        bool replaced = false;
        try { scannedGuard.Verify(CancellationToken.None); } catch (IOException) { replaced = true; }
        Check(replaced, "A physically replaced file invalidates review even when its full content is identical");
        File.Delete(scannedPath); File.Move(scannedPath + ".original", scannedPath);
        File.AppendAllText(scannedPath, "changed after scan"); bool contentChanged = false;
        try { PlannedFile.ReadExpected(scannedPath, scannedIdentity, CancellationToken.None); } catch (IOException) { contentChanged = true; }
        Check(contentChanged, "Content changed between exact scan and preview cannot become a newly authorized cleanup or identify target");
        write(scannedPath, 2, 10); original = Snapshot(source);
        var rename = RenamePlan.Analyze(source, PresetAction.ShuffleAndName, 100, 4, "clip_", 12, MainShell.VideoExtensions, progress, CancellationToken.None, new Random(42));
        Check(original.SequenceEqual(Snapshot(source)), "Rename analysis is mutation-free");
        rename.Execute(progress, CancellationToken.None);
        Check(rename.Changes.All(c => File.Exists(c.Destination) && Hash(c.Destination) == c.File.Hash), "Rename execution uses the exact reviewed random mapping");
        var changed = RenamePlan.Analyze(source, PresetAction.NameByTens, 0, 0, "", 12, MainShell.VideoExtensions, progress, CancellationToken.None);
        File.AppendAllText(changed.Changes[0].File.Path, "changed");
        original = Snapshot(source); bool rejected = false;
        try { changed.Execute(progress, CancellationToken.None); } catch (IOException) { rejected = true; }
        Check(rejected && original.SequenceEqual(Snapshot(source)), "Changed files invalidate a rename review before any mutation");
        var retry = RenamePlan.Analyze(source, PresetAction.ShuffleRandom, 0, 0, "", 12, MainShell.VideoExtensions, progress, CancellationToken.None, new Random(8));
        original = Snapshot(source); using (var cancellation = new CancellationTokenSource())
        {
            bool cancelled = false;
            try { retry.Execute(new TestProgress { OnMessage = _ => cancellation.Cancel() }, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && original.SequenceEqual(Snapshot(source)), "Cancelling a staged rename restores every original path and byte");
        }
        string batches = Directory.CreateDirectory(Path.Combine(parent, "ux-batches")).FullName;
        for (int i = 0; i < 5; i++) write(Path.Combine(batches, $"clip-{i}.avi"), (uint)(i + 1), (byte)(30 + i));
        original = Snapshot(batches);
        var shuffle = ClipBatchShuffler.Analyze(batches, TimeSpan.FromSeconds(5), MainShell.VideoExtensions, progress, CancellationToken.None, new Random(11));
        Check(original.SequenceEqual(Snapshot(batches)) && Directory.GetDirectories(batches).Length == 0, "Shuffle Batches analysis neither moves clips nor creates folders");
        ClipBatchShuffler.Execute(shuffle, progress, CancellationToken.None);
        Check(shuffle.Changes.All(c => File.Exists(c.Destination)), "Shuffle Batches executes the exact reviewed folders and filenames");
        string prepareSource = Directory.CreateDirectory(Path.Combine(parent, "ux-prepare-source")).FullName;
        write(Path.Combine(prepareSource, "1.mp4.avi"), 3, 44); write(Path.Combine(prepareSource, "2.avi"), 4, 45);
        string starter = Directory.CreateDirectory(Path.Combine(destination, "c_10")).FullName;
        write(Path.Combine(starter, "starter.avi"), 2, 44); // different duration/content: keep it
        var draft = new ActionPreset { Action = PresetAction.PrepareBatch, Target = prepareSource, UseWorkingFolder = false, Destination = destination, Selection = BatchSelection.AllVideos, DurationMinutes = 1 };
        original = Snapshot(prepareSource);
        var prepare = PrepareBatchPlan.Analyze(draft, prepareSource, MainShell.VideoExtensions, progress, CancellationToken.None, new Random(4));
        Check(original.SequenceEqual(Snapshot(prepareSource)) && !Directory.Exists(Path.Combine(prepareSource, "Backup")) && prepare.Batch.Path == starter,
            "Prepare Batch analysis resolves the starter folder without making backups or moving files");
        string arrival = Path.Combine(starter, "new-arrival.avi"); write(arrival, 1, 46);
        original = Snapshot(prepareSource); rejected = false;
        try { prepare.Execute(progress, CancellationToken.None); } catch (IOException) { rejected = true; }
        Check(rejected && original.SequenceEqual(Snapshot(prepareSource)), "A new starter video invalidates the reviewed batch before recycling, backing up or moving files");
        File.Delete(arrival);
        prepare.Execute(progress, CancellationToken.None);
        Check(prepare.Changes.All(c => File.Exists(c.Destination) && File.Exists(c.Backup)) && File.Exists(Path.Combine(starter, "starter.avi")), "Prepare Batch executes reviewed moves/backups and preserves starter filenames");
        var offline = new ActionPreset { Target = @"Z:\unavailable", UseWorkingFolder = false, Action = PresetAction.ShuffleRandom };
        offline.ValidateForSave();
        Check(offline.Target == @"Z:\unavailable", "Preset editing accepts an unavailable absolute path without changing execution-time validation");
        string malformedPresetPath = Path.Combine(data, "invalid-presets.json");
        const string malformedPreset = "{\"Presets\":[{\"Id\":\"example\",\"Target\":null}],\"Defaults\":{}}";
        File.WriteAllText(malformedPresetPath, malformedPreset); rejected = false;
        try { PresetStore.Load(malformedPresetPath); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && File.ReadAllText(malformedPresetPath) == malformedPreset, "Malformed preset fields report an error and preserve the original settings for repair");
        Updater(parent);
        TransactionOwnership(parent);
        File.WriteAllText(Path.Combine(data, "ui_settings.json"), "{invalid");
        var prefs = UiPreferences.Load(data, out string warning);
        Check(warning.Length > 0 && File.ReadAllText(Path.Combine(data, "ui_settings.json")) == "{invalid", "Corrupt UI settings use defaults while retaining the original for recovery");
        prefs.Bounds = new Rectangle(-9000, -9000, 9000, 9000); prefs.Theme = AppTheme.Dark;
        prefs.RecentPaths = [@"Z:\temporarily-offline"]; prefs.Save(data);
        var loaded = UiPreferences.Load(data, out warning);
        Check(loaded.Theme == AppTheme.Dark && loaded.RecentPaths.SequenceEqual(prefs.RecentPaths), "New UI preferences preserve unavailable paths and round-trip independently");
        Check(Directory.GetFiles(data, "ui_settings.json.corrupt-*.json").Any(path => File.ReadAllText(path) == "{invalid"), "Saving default UI preferences backs up the original corrupt content");
        string schemaFolder = Directory.CreateDirectory(Path.Combine(data, "unsupported-schema")).FullName;
        File.WriteAllText(Path.Combine(schemaFolder, "ui_settings.json"), "{\"SchemaVersion\":99}");
        UiPreferences.Load(schemaFolder, out warning);
        Check(warning.Length > 0 && File.ReadAllText(Path.Combine(schemaFolder, "ui_settings.json")).Contains("99"), "Unsupported preference versions show a warning and retain the original instead of blocking startup");
        File.WriteAllText(Path.Combine(schemaFolder, "activity.json"), "{\"SchemaVersion\":99,\"Entries\":[]}");
        var unsupportedHistory = new ActivityHistory(schemaFolder);
        Check(unsupportedHistory.Warning.Length > 0 && unsupportedHistory.Entries.Count == 0, "Unsupported activity schemas retain the original and show an empty usable history with a warning");
        Check(new Rectangle(0, 0, 900, 600).Contains(UiPreferences.ClampBounds(prefs.Bounds, new Rectangle(0, 0, 900, 600))), "Restored window geometry clamps to a connected work area");
        var history = new ActivityHistory(data);
        for (int i = 0; i < 140; i++) history.Add(new("test", "scope", DateTimeOffset.Now, null, JobState.Running, "in progress"));
        history.Save(data); var restored = new ActivityHistory(data);
        Check(restored.Entries.Count == 100 && restored.Entries.All(e => e.State == JobState.Interrupted), "Activity remains bounded and interrupted work never restores as success");
        Ui(prefs, data, source);
    }

    private static void Ui(UiPreferences prefs, string data, string source)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Control.CheckForIllegalCrossThreadCalls = true;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                string images = Path.GetFullPath("docs/ux/screenshots"); Directory.CreateDirectory(images);
                WatchFolderSettings.Save(data, []);
                using (var old = new Form1(data, false)) { old.Show(); Application.DoEvents(); old.Hide(); }
                string archiveDestination = Directory.CreateDirectory(Path.Combine(data, "continuous-archive")).FullName;
                new ArchiveSettings { Sources = [source], Destination = archiveDestination, Speed = ArchiveSpeed.Fast,
                    WatchContinuously = true, SettlingSeconds = 0 }.Save(data);
                prefs.Bounds = new Rectangle(50, 50, 1180, 760); prefs.Theme = AppTheme.Light; prefs.Save(data);
                File.WriteAllText(Path.Combine(data, "working_folder.txt"), source);
                var timer = Stopwatch.StartNew();
                using var shell = new MainShell(data); shell.Show(); Application.DoEvents();
                Console.WriteLine($"MEASURE: redesigned shell first screen {timer.ElapsedMilliseconds} ms; private memory {Process.GetCurrentProcess().PrivateMemorySize64 / 1_000_000d:0.0} MB.");
                Check(shell.Visible && shell.WindowState != FormWindowState.Minimized, "First launch opens visibly without modal setup or startup registration");
                var watcherTimeout = Stopwatch.StartNew();
                while (shell.Archive.Status.Copied < 2)
                {
                    if (watcherTimeout.ElapsedMilliseconds > 10000) throw new Exception("New shell watch resumption timed out.");
                    Application.DoEvents(); Thread.Sleep(5);
                }
                Check(shell.Archive.Running && ArchiveSettings.Load(data).WatchContinuously, "New shell resumes saved watching without changing Archive enablement or source roots");
                shell.Navigate("Archive"); shell.Archive.Pause(); Capture(shell, Path.Combine(images, "archive-paused.png"));
                Check(shell.Archive.Status.Paused && ArchiveSettings.Load(data).WatchContinuously, "Pausing the new shell Archive retains continuous watch enablement");
                shell.Archive.Resume();
                foreach (string page in new[] { "Overview", "Archive", "Batches", "Find & clean", "Rename & shuffle", "Activity", "Settings" })
                {
                    timer.Restart(); shell.Navigate(page); Application.DoEvents();
                    Console.WriteLine($"MEASURE: navigate {page}: {timer.ElapsedMilliseconds} ms.");
                    Capture(shell, Path.Combine(images, page.Replace(" & ", "-").Replace(" ", "-").ToLowerInvariant() + "-light.png"));
                }
                foreach (var action in Enum.GetValues<PresetAction>().Where(a => a != PresetAction.Archive)) shell.OpenTool(action);
                Check(Enum.GetValues<PresetAction>().All(a => a == PresetAction.Archive || Descendants(shell).OfType<WorkflowWorkspace>().Any(w => w.Action == a)), "All nine actions remain reachable through integrated workspaces");
                string comparison = Directory.CreateDirectory(Path.Combine(data, "comparison")).FullName;
                string reference = Directory.GetFiles(source, "*.avi")[0];
                File.Copy(reference, Path.Combine(comparison, "other-copy.avi"));
                File.Copy(reference, Path.Combine(source, "duplicate.avi"));
                var snapshot = Snapshot(source);
                var toolDraft = new ActionPreset { Target = source, UseWorkingFolder = false, Destination = comparison, DirectoryB = comparison,
                    ReferenceClip = reference, Selection = BatchSelection.AllVideos, DurationMinutes = 1, Start = 100, Padding = 4 };
                foreach (var workspace in Descendants(shell).OfType<WorkflowWorkspace>().ToArray())
                {
                    toolDraft.Action = workspace.Action; workspace.ApplyPreset(toolDraft);
                    shell.OpenTool(workspace.Action);
                    var analysis = workspace.AnalyzeAsync();
                    while (!analysis.IsCompleted) { Application.DoEvents(); Thread.Sleep(2); }
                    analysis.GetAwaiter().GetResult();
                    var contexts = Descendants(workspace).OfType<FileContextList>().Single();
                    Console.WriteLine($"MEASURE: {workspace.Action} review: {contexts.Bounds}, visible {contexts.Visible}, rows {contexts.Rows.Count}; table {contexts.Table.Bounds}, visible {contexts.Table.Visible}, handle {contexts.Table.IsHandleCreated}; location {contexts.PointToScreen(Point.Empty)}.");
                    Console.WriteLine($"MEASURE: native table visible {IsWindowVisible(contexts.Table.Handle)}; correct table at point {WindowFromPoint(contexts.Table.PointToScreen(new Point(20,20))) == contexts.Table.Handle}; actual row count {contexts.Table.RowCount}; DPI {shell.DeviceDpi}.");
                    if (workspace.Action == PresetAction.SeekDuplications)
                    {
                        using var renderedGrid = new Bitmap(contexts.Width, contexts.Height);
                        contexts.DrawToBitmap(renderedGrid, contexts.ClientRectangle);
                        renderedGrid.Save(Path.Combine(images, "embedded-grid.png"));
                    }
                    Check(contexts.Visible && contexts.Width > 400 && contexts.Height > 200, "Review controls remain visible inside the selected workflow");
                    Check(contexts.Rows.Count > 0, workspace.Action + ": valid configuration produces inspectable planned file rows");
                    var tabs = Descendants(workspace).OfType<DesktopButton>().Where(b => b.Kind == ButtonKind.Tab).ToArray();
                    var configure = tabs.Single(b => b.Text == "Configure");
                    var reviewTab = tabs.Single(b => b.Text == "Review files");
                    Check(reviewTab.Selected && !configure.Selected, workspace.Action + ": analysis activates the Review files tab");
                    configure.PerformClick(); Application.DoEvents();
                    Check(configure.Selected && !reviewTab.Selected && !contexts.Visible, workspace.Action + ": Configure switches back without discarding review rows");
                    reviewTab.PerformClick(); Application.DoEvents();
                    Check(contexts.Visible && contexts.Rows.Count > 0 && reviewTab.Selected, workspace.Action + ": Review files restores the retained preview");
                    Check(snapshot.SequenceEqual(Snapshot(source)), workspace.Action + ": workspace analysis preserves the configured source files");
                    Capture(shell, Path.Combine(images, "review-" + workspace.Action.ToString().ToLowerInvariant() + ".png"));
                }
                using (var started = new ManualResetEventSlim())
                {
                    var generatedLoad = shell.RunJobAsync("Generated progress burst", source, true, (p, token) =>
                    {
                        for (int i = 0; i < 100000; i++) p.Report(i, 100000, "Hashing synthetic fixture");
                        started.Set(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested();
                        return JobOutcome.Complete("unused");
                    });
                    while (!started.IsSet) { Application.DoEvents(); Thread.Sleep(2); }
                    timer.Restart(); shell.Navigate("Overview"); Application.DoEvents();
                    Check(timer.ElapsedMilliseconds < 100, "Warm navigation acknowledges within 100 ms during a 100,000-report burst");
                    var rejectedJob = shell.RunJobAsync("Conflicting command", "fixture", false, (_, _) => throw new Exception("Must not execute"));
                    Check(rejectedJob.GetAwaiter().GetResult() is null, "A conflicting foreground command cannot start a second job");
                    var controller = (JobController)typeof(MainShell).GetField("jobs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
                    controller.Cancel();
                    while (!generatedLoad.IsCompleted) { Application.DoEvents(); Thread.Sleep(2); }
                    Check(generatedLoad.Result?.State == JobState.Cancelled, "Cancellation reports a final Cancelled state after the worker actually stops");
                }
                foreach (AppTheme theme in new[] { AppTheme.Light, AppTheme.Dark, AppTheme.System })
                {
                    DesktopTheme.Current = theme; DesktopTheme.Apply(shell); shell.Navigate("Overview"); Application.DoEvents();
                    Capture(shell, Path.Combine(images, "overview-" + theme.ToString().ToLowerInvariant() + ".png"));
                    shell.Navigate("Settings"); Application.DoEvents();
                    Descendants(shell).OfType<ComboBox>().Single(c => c.AccessibleName == "Theme").SelectedIndex = (int)theme;
                    Application.DoEvents();
                    var settingsTabs = Descendants(shell).OfType<TabControl>().Single(t => t.AccessibleName == "Settings sections");
                    foreach (TabPage section in settingsTabs.TabPages)
                    {
                        shell.OpenSettingsSection(section.Text); Application.DoEvents();
                        foreach (TabPage page in new[] { section }.Concat(Descendants(section).OfType<TabPage>()))
                        {
                            Check(!page.UseVisualStyleBackColor && Contrast(page.ForeColor, page.BackColor) >= 4.5,
                                $"{theme} Settings {section.Text} / {page.Text} has readable themed tab-page colors");
                        }
                        using var rendered = new Bitmap(section.Width, section.Height);
                        section.DrawToBitmap(rendered, section.ClientRectangle);
                        Check(Contrast(section.ForeColor, rendered.GetPixel(8, 48)) >= 4.5,
                            $"{theme} Settings {section.Text} paints a readable background instead of the native tab's white surface");
                        Capture(shell, Path.Combine(images, "settings-" + theme.ToString().ToLowerInvariant() + "-" + section.Text.Replace(" ", "-").ToLowerInvariant() + ".png"));
                    }
                    shell.OpenSettingsSection("General");
                    Capture(shell, Path.Combine(images, "settings-" + theme.ToString().ToLowerInvariant() + ".png"));
                }
                DesktopTheme.Current = AppTheme.Dark; DesktopTheme.Apply(shell);
                shell.OpenTool(PresetAction.ShuffleAndName); Application.DoEvents();
                var renameWorkspace = Descendants(shell).OfType<WorkflowWorkspace>().Single(w => w.Action == PresetAction.ShuffleAndName);
                Descendants(renameWorkspace).OfType<DesktopButton>().Single(b => b.Kind == ButtonKind.Tab && b.Text == "Configure").PerformClick();
                Capture(shell, Path.Combine(images, "rename-shuffle-dark.png"));
                shell.Size = new Size(900, 600); Application.DoEvents();
                var toolButtons = Descendants(shell).OfType<DesktopButton>().Where(b => b.Visible && b.Kind == ButtonKind.Tool).ToArray();
                var viewTabs = Descendants(renameWorkspace).OfType<DesktopButton>().Where(b => b.Kind == ButtonKind.Tab).ToArray();
                Check(toolButtons.Max(b => b.PointToScreen(Point.Empty).Y + b.Height) < viewTabs.Min(b => b.PointToScreen(Point.Empty).Y),
                    "Compact layout keeps tool choices visibly separated from workspace tabs");
                Capture(shell, Path.Combine(images, "rename-shuffle-compact.png"));
                shell.Size = new Size(900, 600); shell.Navigate("Archive"); Application.DoEvents(); Capture(shell, Path.Combine(images, "archive-compact.png"));
                foreach (PresetAction action in Enum.GetValues<PresetAction>())
                {
                    using var editor = new PresetEditorForm(new ActionPreset { Action = action }, source);
                    editor.Show(); Application.DoEvents(); Capture(editor, Path.Combine(images, "preset-" + action.ToString().ToLowerInvariant() + ".png"));
                }
                using (var confirmation = new ReviewConfirmation("Recycle fixture", comparison,
                    [new("fixture", "1", "Recycle", reference, "Keeper: " + Path.Combine(comparison, "other-copy.avi"), "A / batch_0001", "0h 00m 02s", "0.1 MB", Bytes: 100000)], "Cancellation retains earlier successful recycling."))
                { confirmation.Show(); Application.DoEvents(); Capture(confirmation, Path.Combine(images, "exact-confirmation.png")); }
                using var viewer = new Form { ClientSize = new Size(1000, 650), Font = shell.Font };
                var grid = new FileContextList { Dock = DockStyle.Fill };
                var rows = Enumerable.Range(0, 10000).Select(i => new ContextRow("key-" + i, (i / 3).ToString(), "Keep", @"C:\library\very long folder context\clip-" + i + ".mp4", "Keep / Recycle", "A / batch_" + i / 3, "1h 12m 08s", "150 MB", true)).ToArray();
                grid.SetRows(rows); grid.SelectedKeys.Add("key-9999"); grid.SetFilter("clip-1.");
                Check(grid.SelectedKeys.SetEquals(["key-9999"]) && grid.Table.RowCount == 1, "Filtered 10,000-row review retains hidden selection by stable identity");
                grid.SetRows(rows);
                Check(grid.SelectedKeys.SetEquals(["key-9999"]), "Refreshing unchanged review rows preserves hidden file choices");
                grid.SetRows(rows.Select(r => r.Key == "key-9999" ? r with { Identity = "replacement-content" } : r).ToArray());
                Check(grid.SelectedKeys.Count == 0, "Refreshing a replaced file clears its previous recycle choice");
                grid.SetFilter(""); viewer.Controls.Add(grid); DesktopTheme.Apply(viewer); viewer.Show(); Application.DoEvents();
                Capture(viewer, Path.Combine(images, "duplicate-review-prototype.png"));
                var longRows = Enumerable.Range(0, 50000).Select(i => rows[i % rows.Length] with { Key = i.ToString() }).ToArray();
                timer.Restart(); grid.SetRows(longRows); Application.DoEvents();
                Console.WriteLine($"MEASURE: virtual 50,000-entry catalog rendered in {timer.ElapsedMilliseconds} ms; private memory {Process.GetCurrentProcess().PrivateMemorySize64 / 1_000_000d:0.0} MB.");
                Check(grid.Table.VirtualMode && grid.Table.RowCount == 50000, "Large catalog renders through virtual cells");
                using var signal = new InstanceActivation("ClipManagerTest-" + Guid.NewGuid()); int activations = 0;
                signal.Listen(shell, () => { activations++; shell.ActivateExisting(); }); shell.Hide(); signal.Signal();
                var timeout = Stopwatch.StartNew();
                while (activations == 0 && timeout.ElapsedMilliseconds < 3000) { Application.DoEvents(); Thread.Sleep(5); }
                Check(activations == 1 && shell.Visible, "Single-instance IPC activates the existing window without creating workers");
                var sameWorker = shell.Archive; shell.Close(); Application.DoEvents();
                Check(!shell.Visible && shell.Archive.Running, "Closing to tray leaves the new shell's continuous watcher running");
                shell.ActivateExisting();
                Check(ReferenceEquals(sameWorker, shell.Archive), "Hide and activation retain one Archive worker instance");
                var stopArchive = (Task)typeof(MainShell).GetMethod("StopArchiveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, [])!;
                while (!stopArchive.IsCompleted) { Application.DoEvents(); Thread.Sleep(5); }
                stopArchive.GetAwaiter().GetResult();
                Check(!shell.Archive.Running && !ArchiveSettings.Load(data).WatchContinuously, "New shell Stop drains Archive and persists watch auto-resume disabled");
                var shellHistory = (ActivityHistory)typeof(MainShell).GetField("history", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
                Check(shellHistory.Entries.Any(e => e.Action == "Archive" && e.Finished is not null
                    && e.State is JobState.Cancelled or JobState.CompletedWithIssues
                    && e.Details.Contains("Copied and verified: " + shell.Archive.Status.Copied)),
                    "Background Archive stop records an accurate retained-copy outcome in the shared Activity history");
                shell.Navigate("Archive");
                var archivePage = Descendants(shell).OfType<ArchiveWorkspace>().Single();
                var archiveSettings = ArchiveSettings.Load(data);
                archiveSettings.VerifyExistingContent = true; archiveSettings.Speed = ArchiveSpeed.Fast; archiveSettings.SettlingSeconds = 0;
                archivePage.Apply(archiveSettings);
                var archiveMode = Descendants(archivePage).OfType<ComboBox>().Single(c => c.AccessibleName == "Archive run mode");
                Check(archiveMode.SelectedIndex == 2, "Archive exposes a separate full-content recheck mode");
                var archiveStart = Descendants(archivePage).OfType<Button>().Single(b => b.Text == "Start");
                archiveStart.PerformClick();
                timeout.Restart();
                while (shell.Archive.Running && timeout.ElapsedMilliseconds < 10000) { Application.DoEvents(); Thread.Sleep(5); }
                Check(!shell.Archive.Running && shell.Archive.Settings.VerifyExistingContent && shell.Archive.Status.AlreadyArchived > 0
                    && shell.Archive.Status.Unchanged == 0 && shell.Archive.Status.Failed == 0 && !ArchiveSettings.Load(data).WatchContinuously,
                    "Starting full recheck from the desktop reads existing content and persists run-once configuration");
                archivePage.RefreshStatus(); archiveMode.SelectedIndex = 0; archiveStart.PerformClick();
                timeout.Restart();
                while (shell.Archive.Running && timeout.ElapsedMilliseconds < 10000) { Application.DoEvents(); Thread.Sleep(5); }
                Check(!shell.Archive.Running && !ArchiveSettings.Load(data).VerifyExistingContent && shell.Archive.Status.Unchanged > 0
                    && shell.Archive.Status.AlreadyArchived == 0 && shell.Archive.Status.Copied == 0 && shell.Archive.Status.Failed == 0,
                    "Switching the desktop back to Run once uses saved receipts and skips unchanged clip content");
                archivePage.RefreshStatus(); Capture(shell, Path.Combine(images, "archive-cached-run.png"));
                var exit = (Task)typeof(MainShell).GetMethod("ExitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, [false])!;
                while (!exit.IsCompleted) { Application.DoEvents(); Thread.Sleep(5); }
                exit.GetAwaiter().GetResult();
                Check(!shell.Archive.Running, "Responsive shutdown drains background work before closing");
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw new Exception("UX checks failed", error);
    }
    private static IEnumerable<Control> Descendants(Control parent)
    { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void Updater(string parent)
    {
        string folder = Directory.CreateDirectory(Path.Combine(parent, "update ['quoted'] $literal `tick & (scope)")).FullName;
        string application = Path.Combine(folder, "existing.exe");
        string log = Path.Combine(folder, "recovery.log");
        File.WriteAllText(application, "original disposable executable fixture");
        string script = Path.Combine(folder, "update.ps1");
        File.WriteAllText(script, AppUpdater.BuildUpdateScript(int.MaxValue,
            Path.Combine(folder, "missing 'file' $(New-Item injected.txt) `x.exe"), application, log));
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = folder };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(15000)) { process.Kill(); throw new Exception("Disposable updater recovery fixture timed out."); }
        Check(File.ReadAllText(application) == "original disposable executable fixture" && File.Exists(log)
            && Directory.GetFiles(folder, "*.update-backup").Length == 0 && !File.Exists(Path.Combine(folder, "injected.txt")),
            "Updater handles quoted/literal paths, restores the original on replacement failure, and records recovery without evaluating path contents");
        bool rejected = false;
        try { AppUpdater.ApplyUpdate(application); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Updater rejects an invalid executable before launching its replacement helper");
    }
    private static void TransactionOwnership(string parent)
    {
        string folder = Directory.CreateDirectory(Path.Combine(parent, "ux-ownership")).FullName;
        string original = Path.Combine(folder, "original.avi"); File.WriteAllText(original, "owned fixture");
        using (var transaction = new OperationTransaction())
        {
            OperationTransaction.Recycle(original);
            string staged = Directory.GetFiles(folder, "*.pending").Single();
            File.Move(staged, staged + ".retained"); File.WriteAllText(staged, "external replacement");
            bool rejected = false;
            try { transaction.Rollback(); } catch (IOException) { rejected = true; }
            Check(rejected && File.ReadAllText(staged) == "external replacement" && File.ReadAllText(staged + ".retained") == "owned fixture",
                "Rollback leaves an external staging replacement untouched and reports retained recovery content");
        }
        File.WriteAllText(original, "second owned fixture");
        using (var transaction = new OperationTransaction())
        {
            OperationTransaction.Recycle(original); string? recovery = null;
            transaction.Commit(CancellationToken.None, staged =>
            {
                recovery = staged + ".recovery"; File.Move(recovery, recovery + ".retained");
                File.WriteAllText(recovery, "external recovery replacement"); File.Delete(staged);
            });
            Check(recovery is not null && File.ReadAllText(recovery) == "external recovery replacement" && transaction.CleanupWarnings.Count == 1,
                "Committed cleanup never deletes another writer's recovery replacement and exposes its exact path");
        }
    }
    private static void Capture(Form form, string path)
    {
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        if (!form.Visible) form.Show();
        if (form is MainShell shell) typeof(MainShell).GetMethod("RefreshPresentation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, []);
        form.BringToFront(); form.Activate(); form.Refresh(); Application.DoEvents();
        foreach (Control child in Descendants(form)) if (child.Visible) child.Refresh();
        Thread.Sleep(60); Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        using var graphics = Graphics.FromImage(bitmap);
        IntPtr device = graphics.GetHdc(); bool captured;
        try { captured = PrintWindow(form.Handle, device, 2); }
        finally { graphics.ReleaseHdc(device); }
        if (!captured) form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path);
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        double first = Luminance(a), second = Luminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }
    private static string[] Snapshot(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(path, p) + ":" + Hash(p)).Order().ToArray();
    private static void Check(bool condition, string description)
    { if (!condition) throw new Exception(description); Console.WriteLine("PASS: " + description); }
}
