using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using VideoTrayApp;

internal static class MonthlyBatchChecks
{
    internal static void Run(string parent, Action<string, uint, byte> write)
    {
        string source = Directory.CreateDirectory(Path.Combine(parent, "monthly-source")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
        string output = Directory.CreateDirectory(Path.Combine(parent, "monthly-output")).FullName;
        var month = new DateOnly(2025, 3, 1); var today = new DateOnly(2025, 4, 15);
        string first = Path.Combine(source, "original.mp4"), second = Path.Combine(nested, "short.MP4");
        WriteDated(first, 5, 61, new(2025, 3, 4, 12, 0, 0, TimeSpan.Zero), write);
        WriteDated(second, 2, 62, new(2025, 3, 21, 12, 0, 0, TimeSpan.Zero), write);
        File.Copy(first, Path.Combine(nested, "renamed.mp4"));
        write(Path.Combine(source, "unknown.mp4"), 2, 63);
        WriteDated(Path.Combine(source, "previous.mp4"), 2, 64, new(2025, 2, 10, 12, 0, 0, TimeSpan.Zero), write);
        WriteDated(Path.Combine(source, "next.mp4"), 2, 65, new(2025, 4, 10, 12, 0, 0, TimeSpan.Zero), write);
        File.Copy(first, Path.Combine(source, "ignored.mov"));
        File.SetCreationTimeUtc(first, new DateTime(2026, 1, 1)); File.SetLastWriteTimeUtc(first, new DateTime(2026, 2, 2));
        var before = Snapshot(source);
        MonthlyBatchPlan Analyze(DateOnly? selected = null) => MonthlyBatchPreparer.Analyze(source, output, selected ?? month,
            new TestProgress(), CancellationToken.None, today: today, timeZone: TimeZoneInfo.Utc);
        var plan = Analyze();
        Check(plan.Clips.Count == 2 && plan.TotalDuration == TimeSpan.FromSeconds(7) && plan.Duplicates == 1
            && plan.Unknown == 1 && plan.Scanned == 6 && plan.Clips[0].File.Path == second
            && plan.Clips.Select(c => c.Name).SequenceEqual(["0001.MP4", "0002.mp4"]),
            "Monthly preparation uses original embedded dates, recursive MP4 selection, exact deduplication and duration ordering");
        Check(before.SequenceEqual(Snapshot(source)) && !Directory.EnumerateFileSystemEntries(output).Any(),
            "Monthly analysis freezes exact paths and duration without changing source or output");
        foreach (var invalid in new[] { new DateOnly(2025, 4, 1), new DateOnly(2027, 1, 1) })
            Reject<ArgumentException>(() => Analyze(invalid), "Current and future months are rejected by the service");
        Reject<IOException>(() => MonthlyBatchPreparer.Analyze(source, nested, month, new TestProgress(), CancellationToken.None),
            "Nested or overlapping source/output directories cannot feed generated batches back into the archive");
        Check(Analyze(new(2020, 1, 1)).Execute(new TestProgress(), CancellationToken.None).Count == 0
            && !Directory.EnumerateFileSystemEntries(output).Any(), "Empty historical months do not create empty batches");

        using (var cancel = new CancellationTokenSource())
            Reject<OperationCanceledException>(() => plan.Execute(new TestProgress { OnReport = n => { if (n == 1) cancel.Cancel(); } }, cancel.Token),
                "Cancellation after copying a clip stops monthly preparation");
        Check(!Directory.EnumerateFileSystemEntries(output).Any() && before.SequenceEqual(Snapshot(source)),
            "Cancelled monthly preparation cleans staged copies and preserves all originals");
        string reviewedPath = plan.Clips[^1].File.Path;
        File.AppendAllText(reviewedPath, "external change");
        Reject<IOException>(() => plan.Execute(new TestProgress(), CancellationToken.None), "Changes after monthly review reject publication");
        Check(!Directory.EnumerateFileSystemEntries(output).Any(), "Failed monthly preparation removes earlier verified staging copies");
        WriteDated(reviewedPath, 5, 61, new(2025, 3, 4, 12, 0, 0, TimeSpan.Zero), write);
        plan = Analyze(); before = Snapshot(source);
        var result = plan.Execute(new TestProgress(), CancellationToken.None);
        Check(result.Count == 2 && result.Duration == TimeSpan.FromSeconds(7) && result.Summary.Contains("0h 00m 07s")
            && plan.Clips.All(c => Hash(Path.Combine(result.Directory, c.Name)) == c.File.Hash)
            && before.SequenceEqual(Snapshot(source)) && File.Exists(Path.Combine(result.Directory, MonthlyBatchPreparer.ManifestName)),
            "Monthly execution publishes verified copies, original creation dates and playback duration while preserving sources");
        Check(Analyze().Destination.EndsWith("best_of_2025-03_2"), "Repeated manual preparation selects a new collision-safe batch folder");
        var settings = new MonthlyBatchSettings { Enabled = true, ArchiveDirectory = source, OutputDirectory = output, NextMonth = month };
        var recovered = MonthlyBatchPreparer.PrepareAutomatic(settings, month, new TestProgress(), CancellationToken.None, today);
        Check(recovered.Reused && recovered.Count == 2 && Directory.GetDirectories(output).Length == 1,
            "Automatic restart recovers a published batch from its verified manifest without duplicating output");
        string changed = Path.Combine(result.Directory, "0001.MP4"); File.AppendAllText(changed, "changed");
        Reject<IOException>(() => MonthlyBatchPreparer.PrepareAutomatic(settings, month, new TestProgress(), CancellationToken.None, today),
            "Automatic recovery refuses modified completed batches instead of overwriting them");
        Check(File.ReadAllText(changed).EndsWith("changed") && Directory.GetDirectories(output).Length == 1,
            "Modified completed output is retained for inspection");
        string occupiedOutput = Directory.CreateDirectory(Path.Combine(parent, "monthly-occupied")).FullName;
        string occupied = Directory.CreateDirectory(Path.Combine(occupiedOutput, "best_of_2025-03")).FullName;
        File.WriteAllText(Path.Combine(occupied, "keep.txt"), "keep"); settings.OutputDirectory = occupiedOutput;
        Reject<IOException>(() => MonthlyBatchPreparer.PrepareAutomatic(settings, month, new TestProgress(), CancellationToken.None, today),
            "Unrelated folders in an automatic destination are retained and reported");
        string invalidPath = Path.Combine(source, "broken.mp4"); File.WriteAllBytes(invalidPath, [0, 0, 0]);
        Reject<IOException>(() => Analyze(), "Incomplete monthly scans cannot publish a partial batch"); File.Delete(invalidPath);

        Boundaries(parent, write);
        Schedule(parent, source, output);
        ArchiveReadiness(parent, write);
        Ui(parent, source, write);
    }

    private static void Boundaries(string parent, Action<string, uint, byte> write)
    {
        string source = Directory.CreateDirectory(Path.Combine(parent, "monthly-boundaries")).FullName;
        string output = Directory.CreateDirectory(Path.Combine(parent, "monthly-boundaries-output")).FullName;
        WriteDated(Path.Combine(source, "first.mp4"), 1, 71, new(2024, 2, 29, 16, 0, 0, TimeSpan.Zero), write);
        WriteDated(Path.Combine(source, "last.mp4"), 2, 72, new(2024, 3, 31, 15, 59, 59, TimeSpan.Zero), write);
        WriteDated(Path.Combine(source, "outside.mp4"), 3, 73, new(2024, 3, 31, 16, 0, 0, TimeSpan.Zero), write);
        var zone = TimeZoneInfo.CreateCustomTimeZone("Monthly UTC+8", TimeSpan.FromHours(8), "Monthly UTC+8", "Monthly UTC+8");
        var plan = MonthlyBatchPreparer.Analyze(source, output, new(2024, 3, 17), new TestProgress(), CancellationToken.None,
            today: new(2024, 4, 1), timeZone: zone);
        Check(plan.Month == new DateOnly(2024, 3, 1) && plan.Clips.Count == 2 && plan.TotalDuration == TimeSpan.FromSeconds(3),
            "Monthly boundaries include local midnight and the last second, handle leap years and exclude the next month");
    }

    private static void Schedule(string parent, string source, string output)
    {
        string data = Directory.CreateDirectory(Path.Combine(parent, "monthly-settings")).FullName;
        var settings = new MonthlyBatchSettings { Enabled = true, ArchiveDirectory = source, OutputDirectory = output };
        Check(settings.NotifyOnCompletion && settings.DueMonth(new(2026, 1, 1)) == new DateOnly(2025, 12, 1),
            "Automatic setup starts with the previous completed month across year boundaries and enables duration notifications");
        settings.NextMonth = new(2025, 11, 1); settings.Save(data); settings = MonthlyBatchSettings.Load(data);
        Check(settings.NextMonth == new DateOnly(2025, 11, 1) && settings.DueMonth(new(2026, 1, 1)) == settings.NextMonth,
            "Saved schedule catches up missed completed months in order after restart");
        settings.NextMonth = new(2026, 1, 1);
        Check(settings.DueMonth(new(2026, 1, 31)) is null, "Automatic preparation never processes the current month");
        settings.Enabled = false;
        Check(settings.DueMonth(new(2026, 2, 1)) is null, "Disabling automatic preparation stops future scheduled work");
    }

    private static void ArchiveReadiness(string parent, Action<string, uint, byte> write)
    {
        string source = Directory.CreateDirectory(Path.Combine(parent, "monthly-ready-source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(parent, "monthly-ready-archive")).FullName;
        string data = Directory.CreateDirectory(Path.Combine(parent, "monthly-ready-data")).FullName;
        write(Path.Combine(source, "pending.mp4"), 3, 85);
        using var coordinator = new OperationCoordinator();
        using var worker = new ArchiveWorker(coordinator, MainShell.VideoExtensions, data);
        using (coordinator.ForegroundAsync().GetAwaiter().GetResult())
        {
            worker.Start(new ArchiveSettings { Sources = [source], Destination = destination, WatchContinuously = true,
                Speed = ArchiveSpeed.Fast, SettlingSeconds = 0 });
            Check(worker.Status.Queued == 0 && !worker.ReadyForMonthlyBatch,
                "Automatic monthly preparation waits for archive discovery even before pending copies have been queued");
        }
        var timeout = Stopwatch.StartNew();
        while (!worker.ReadyForMonthlyBatch)
        { if (timeout.ElapsedMilliseconds > 10000) throw new TimeoutException("Archive readiness timed out."); Thread.Sleep(10); }
        Check(worker.Status.Copied == 1 && Directory.GetFiles(destination, "*.mp4").Length == 1,
            "Archive becomes ready for monthly selection only after source discovery and copying have finished");
        worker.StopAsync().GetAwaiter().GetResult();
    }

    private static void Ui(string parent, string source, Action<string, uint, byte> write)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                Control.CheckForIllegalCrossThreadCalls = true;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                string data = Directory.CreateDirectory(Path.Combine(parent, "monthly-ui-data")).FullName;
                string output = Directory.CreateDirectory(Path.Combine(parent, "monthly-ui-output")).FullName;
                new UiPreferences { SetupDismissed = true, CloseToTray = false }.Save(data);
                File.WriteAllText(Path.Combine(data, "working_folder.txt"), source);
                using var shell = new MainShell(data); shell.Show(); Application.DoEvents();
                shell.Navigate("Batches"); Descendants(shell).OfType<Button>().Single(b => b.Text == "Best of the month").PerformClick();
                var workspace = Descendants(shell).OfType<MonthlyBatchWorkspace>().Single();
                var picker = Descendants(workspace).OfType<DateTimePicker>().Single();
                Check(workspace.Visible && picker.MaxDate == new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddDays(-1),
                    "Best of the month is reachable under Batches and its native month picker excludes current/future months");
                var paths = Descendants(workspace).OfType<PathPicker>().ToArray(); paths[0].Value = source; paths[1].Value = output;
                Capture(shell, "configuration");
                picker.Value = new(2025, 3, 1); Pump(workspace.AnalyzeAsync());
                var list = Descendants(workspace).OfType<FileContextList>().Single();
                Check(list.Visible && list.Rows.Count == 2 && list.Rows.All(r => r.Action == "Copy" && r.Created.HasValue)
                    && list.Table.Columns["Created"]!.Visible
                    && !Descendants(workspace).OfType<Button>().Any(b => b.Visible && b.Text.Contains("Recycle")),
                    "Monthly native review shows exact copy paths, original creation dates and unique clips");
                foreach (var size in new[] { new Size(1180, 760), new Size(640, 480) })
                {
                    shell.ClientSize = size; Application.DoEvents();
                    Check(list.Visible && list.Table.Width > 250, $"Monthly review remains reachable at {size.Width}x{size.Height}");
                    if (size.Width == 640)
                    {
                        workspace.AutoScrollPosition = new Point(0, 150); Application.DoEvents();
                        Rectangle exposed = list.Table.RectangleToScreen(list.Table.ClientRectangle);
                        for (Control? ancestor = list.Table.Parent; ancestor is not null; ancestor = ancestor.Parent)
                            exposed = Rectangle.Intersect(exposed, ancestor.RectangleToScreen(ancestor.ClientRectangle));
                        Check(exposed.Height >= 80, "Compact monthly review can scroll to a usable table viewport");
                        Capture(shell, "compact");
                    }
                }
                if (Environment.GetEnvironmentVariable("CLIP_MONTHLY_SCREENSHOT") is { Length: > 0 } screenshot)
                {
                    shell.ClientSize = new(1180, 760); Application.DoEvents();
                    using var bitmap = new Bitmap(shell.Width, shell.Height); shell.DrawToBitmap(bitmap, new(Point.Empty, shell.Size)); bitmap.Save(screenshot);
                }
                void Prepare(DialogResult response)
                {
                    bool confirmed = false;
                    using var timer = new System.Windows.Forms.Timer { Interval = 10 };
                    timer.Tick += (_, _) =>
                    {
                        if (Application.OpenForms.OfType<ReviewConfirmation>().FirstOrDefault() is not { } dialog) return;
                        confirmed = true; dialog.DialogResult = response;
                    };
                    timer.Start(); Pump(workspace.PrepareAsync());
                    Check(confirmed, "Manual monthly preparation presents an exact-file confirmation");
                }
                Prepare(DialogResult.Cancel);
                Check(!Directory.GetDirectories(output).Any(), "Cancelling monthly confirmation leaves the output unchanged");
                Prepare(DialogResult.OK);
                Check(Directory.GetFiles(Path.Combine(output, "best_of_2025-03"), "*.mp4").Length == 2,
                    "Confirmed monthly preparation creates the reviewed batch through the native UI");

                shell.OpenSettingsSection("Best of the month"); Application.DoEvents();
                var section = Descendants(shell).OfType<TabPage>().Single(t => t.Text == "Best of the month");
                var settingsPaths = Descendants(section).OfType<PathPicker>().ToArray();
                string autoSource = Directory.CreateDirectory(Path.Combine(parent, "monthly-auto-source")).FullName;
                string autoOutput = Directory.CreateDirectory(Path.Combine(parent, "monthly-auto-output")).FullName;
                var previous = MonthlyBatchSettings.MonthStart(DateOnly.FromDateTime(DateTime.Today)).AddMonths(-1);
                WriteDated(Path.Combine(autoSource, "recorded.mp4"), 9, 91, new(previous.Year, previous.Month, 10, 12, 0, 0, TimeSpan.Zero), write);
                settingsPaths[0].Value = autoSource; settingsPaths[1].Value = autoOutput;
                var checks = Descendants(section).OfType<CheckBox>().ToArray(); checks[0].Checked = true; checks[1].Checked = false;
                Capture(shell, "settings");
                Descendants(section).OfType<Button>().Single(b => b.Text == "Save monthly preparation settings").PerformClick();
                Wait(() => MonthlyBatchSettings.Load(data).NextMonth == previous.AddMonths(1) && !shell.IsBusy);
                string autoBatch = Path.Combine(autoOutput, "best_of_" + previous.ToString("yyyy-MM"));
                Check(File.Exists(Path.Combine(autoBatch, "0001.mp4")) && File.ReadAllText(Path.Combine(data, "activity.json")).Contains("0h 00m 09s"),
                    "Enabling monthly settings automatically prepares the previous month and persists its playback duration and progress");
                shell.Close(); Wait(() => shell.IsDisposed);

                // Resume two missed months, then ensure another launch does not duplicate either batch.
                string catchupOutput = Directory.CreateDirectory(Path.Combine(parent, "monthly-catchup-output")).FullName;
                var older = previous.AddMonths(-1);
                WriteDated(Path.Combine(autoSource, "older.mp4"), 4, 92, new(older.Year, older.Month, 10, 12, 0, 0, TimeSpan.Zero), write);
                new MonthlyBatchSettings { Enabled = true, NotifyOnCompletion = false, ArchiveDirectory = autoSource,
                    OutputDirectory = catchupOutput, NextMonth = older }.Save(data);
                using (var resumed = new MainShell(data))
                {
                    resumed.Show(); Wait(() => MonthlyBatchSettings.Load(data).NextMonth == previous.AddMonths(1) && !resumed.IsBusy);
                    Check(Directory.GetDirectories(catchupOutput).Length == 2, "Automatic startup catches up missed months in chronological order");
                    resumed.Close(); Wait(() => resumed.IsDisposed);
                }
                using var restarted = new MainShell(data); restarted.Show(); Application.DoEvents();
                Check(!restarted.IsBusy && Directory.GetDirectories(catchupOutput).Length == 2,
                    "Restarting an up-to-date automatic schedule creates no duplicate monthly batches");
                restarted.Close(); Wait(() => restarted.IsDisposed);

                string watchData = Directory.CreateDirectory(Path.Combine(parent, "monthly-watch-data")).FullName;
                string watchSource = Directory.CreateDirectory(Path.Combine(parent, "monthly-watch-source")).FullName;
                string watchArchive = Directory.CreateDirectory(Path.Combine(parent, "monthly-watch-archive")).FullName;
                string watchOutput = Directory.CreateDirectory(Path.Combine(parent, "monthly-watch-output")).FullName;
                WriteDated(Path.Combine(watchSource, "arriving.mp4"), 11, 93, new(previous.Year, previous.Month, 10, 12, 0, 0, TimeSpan.Zero), write);
                new UiPreferences { SetupDismissed = true, CloseToTray = false }.Save(watchData);
                new ArchiveSettings { Sources = [watchSource], Destination = watchArchive, WatchContinuously = true,
                    Speed = ArchiveSpeed.Fast, SettlingSeconds = 0 }.Save(watchData);
                string idleSource = Directory.CreateDirectory(Path.Combine(parent, "monthly-idle-source")).FullName;
                string idleArchive = Directory.CreateDirectory(Path.Combine(parent, "monthly-idle-archive")).FullName;
                AtomicJson.Save(Path.Combine(watchData, "archive_routes.json"), new ArchiveRouteStore { Routes =
                [
                    new() { Name = "Other clips", ResumeOnStartup = true, Settings = new ArchiveSettings { Sources = [idleSource], Destination = idleArchive,
                        WatchContinuously = true, Speed = ArchiveSpeed.Fast, SettlingSeconds = 0 } },
                    new() { Name = "Monthly source", ResumeOnStartup = true, Settings = ArchiveSettings.Load(watchData) }
                ] });
                new MonthlyBatchSettings { Enabled = true, NotifyOnCompletion = false, ArchiveDirectory = watchArchive,
                    OutputDirectory = watchOutput, NextMonth = previous }.Save(watchData);
                using var watched = new MainShell(watchData); watched.Show();
                Wait(() => MonthlyBatchSettings.Load(watchData).NextMonth == previous.AddMonths(1) && !watched.IsBusy);
                string watchedBatch = Path.Combine(watchOutput, "best_of_" + previous.ToString("yyyy-MM"), "0001.mp4");
                Check(watched.Archives.Routes[1].Worker.Status.Copied == 1 && watched.Archive.Status.Copied == 0 && File.Exists(watchedBatch)
                    && ClipIdentifier.ReadIdentity(watchedBatch, CancellationToken.None).Duration == TimeSpan.FromSeconds(11),
                    "Automatic monthly preparation waits for its matching secondary archive route and includes the newly archived original-month clip");
                watched.Close(); Wait(() => watched.IsDisposed);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw new Exception("Monthly UI check failed.", error);
    }

    private static void Pump(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Capture(MainShell shell, string role)
    {
        if (Environment.GetEnvironmentVariable("CLIP_MONTHLY_SCREENSHOT") is not { Length: > 0 } screenshot) return;
        Application.DoEvents();
        using var bitmap = new Bitmap(shell.Width, shell.Height); shell.DrawToBitmap(bitmap, new(Point.Empty, shell.Size));
        bitmap.Save(Path.Combine(Path.GetDirectoryName(screenshot)!, Path.GetFileNameWithoutExtension(screenshot) + "-" + role + ".png"));
    }
    private static void Wait(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done()) { if (watch.ElapsedMilliseconds > 20000) throw new TimeoutException("Monthly UI operation timed out."); Application.DoEvents(); Thread.Sleep(2); }
    }
    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control child in root.Controls) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
    private static void WriteDated(string path, uint seconds, byte content, DateTimeOffset created, Action<string, uint, byte> write)
    {
        write(path, seconds, content); byte[] bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(44), checked((uint)(created - new DateTimeOffset(1904, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds));
        File.WriteAllBytes(path, bytes);
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string[] Snapshot(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
        .OrderBy(p => p).Select(p => p + ":" + Hash(p)).ToArray();
    private static void Reject<T>(Action action, string description) where T : Exception
    { bool rejected = false; try { action(); } catch (T) { rejected = true; } Check(rejected, description); }
    private static void Check(bool condition, string description)
    { if (!condition) throw new Exception("FAIL: " + description); Console.WriteLine("PASS: " + description); }
}
