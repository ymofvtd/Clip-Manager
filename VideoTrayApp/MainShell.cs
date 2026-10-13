using Microsoft.Win32;

namespace VideoTrayApp;

internal sealed partial class MainShell : Form
{
    internal static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mov", ".m4v", ".mxf", ".avi", ".mkv", ".wmv", ".mts", ".m2ts", ".mpg", ".mpeg", ".3gp", ".flv", ".webm" };
    private readonly string dataFolder;
    private readonly UiPreferences preferences;
    private readonly ActivityHistory history;
    private readonly OperationCoordinator coordinator = new();
    private readonly FolderDurationTracker tracker = new(VideoExtensions);
    private readonly ArchiveWorker archive;
    private readonly JobController jobs;
    private readonly NotifyIcon tray = new();
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 300 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Panel content = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly FlowLayoutPanel navigation = new() { Dock = DockStyle.Left, Width = 204, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12), Tag = SurfaceRole.Sidebar };
    private readonly Label heading = new() { Dock = DockStyle.Top, Height = 76, Font = new Font("Segoe UI", 22, FontStyle.Bold), Padding = new Padding(24, 16, 0, 0), AccessibleName = "Workspace title", UseMnemonic = false };
    private readonly Label jobStatus = new() { AutoSize = true, MaximumSize = new Size(750, 65), AccessibleName = "Current operation" };
    private readonly ProgressBar progress = new() { Width = 160, Height = 20 };
    private readonly Button cancel;
    private readonly Button archiveIndicator;
    private readonly Dictionary<string, Control> pages = [];
    private readonly Dictionary<PresetAction, WorkflowWorkspace> workflows = [];
    private readonly Label overview = DesktopTheme.Text("");
    private readonly DetailsBox activityDetails = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly ListBox activityList = new() { Dock = DockStyle.Top, Height = 220, AccessibleName = "Activity history" };
    private readonly ComboBox activityFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, AccessibleName = "Activity outcome filter" };
    private List<JobRecord> displayedActivity = [];
    private string selected = "Overview", workingFolder = "", persistentWarning = "";
    private string workingSummary = "Folder summary has not been refreshed.";
    private CancellationTokenSource? folderRefresh;
    private List<string> trackingRoots = [];
    private bool shuttingDown, exitReady, startupConfigured;
    private bool resourcesDisposed;
    private Task? trackingInitialization;
    private ArchiveWorkspace? archivePage;
    private JobRecord? archiveActivity;
    internal bool IsBusy => jobs.Busy;
    internal ArchiveWorker Archive => archive;
    internal string DataFolder => dataFolder;

    internal MainShell(string? folder = null)
    {
        dataFolder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipsManager");
        preferences = UiPreferences.Load(dataFolder, out persistentWarning);
        history = new(dataFolder); persistentWarning += "\n" + history.Warning;
        jobs = new(coordinator, history, dataFolder); archive = new(coordinator, VideoExtensions, dataFolder);
        Text = "Clips Manager v" + AppUpdater.CurrentVersion.ToString(3);
        Font = new Font("Segoe UI", 10.5f); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1180, 760); MinimumSize = new Size(640, 480); StartPosition = FormStartPosition.CenterScreen;
        try
        {
            trackingRoots = WatchFolderSettings.Load(dataFolder, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoTrayApp"));
            string path = Path.Combine(dataFolder, "working_folder.txt");
            workingFolder = File.Exists(path) ? File.ReadAllText(path).Trim() : trackingRoots.FirstOrDefault() ?? "";
        }
        catch (Exception ex) { persistentWarning += "\nFolder settings need attention: " + ex.Message; }
        cancel = DesktopTheme.Button("Cancel operation", () => jobs.Cancel());
        archiveIndicator = DesktopTheme.Button("Archive: stopped", () => Navigate("Archive"));
        var strip = DesktopTheme.Actions(archiveIndicator, jobStatus, progress, cancel, DesktopTheme.Button("View activity", () => Navigate("Activity")));
        strip.Dock = DockStyle.Bottom; strip.Padding = new Padding(12); strip.MinimumSize = new Size(0, 65); strip.Tag = SurfaceRole.Inset;
        navigation.Controls.Add(new Label { Text = "CLIP / MANAGER", AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Margin = new Padding(10, 14, 0, 4), Tag = TextRole.Accent });
        navigation.Controls.Add(new Label { Text = "Your library, in order.", AutoSize = true, Font = new Font("Segoe UI", 9),
            Margin = new Padding(10, 0, 0, 28), Tag = TextRole.Secondary });
        foreach (string name in new[] { "Overview", "Archive", "Batches", "Find & clean", "Rename & shuffle", "Activity", "Settings" })
        {
            var button = DesktopTheme.Button(name, () => Navigate(name)); button.Width = 176; button.AutoSize = false; button.Height = 44;
            button.Kind = ButtonKind.Navigation;
            button.Tag = name; navigation.Controls.Add(button);
        }
        Controls.Add(content); Controls.Add(heading); Controls.Add(navigation); Controls.Add(strip);
        BuildOverview(); BuildActivity();
        DesktopTheme.Current = preferences.Theme; DesktopTheme.Apply(this);
        Resize += (_, _) =>
        {
            bool compact = ClientSize.Width * 96 / Math.Max(DeviceDpi, 96) < 1000;
            navigation.Width = compact ? 168 : 204;
            foreach (Button button in navigation.Controls.OfType<Button>()) button.Width = navigation.Width - 28;
            jobStatus.MaximumSize = new Size(Math.Max(220, ClientSize.Width - 620), 65);
        };
        refresh.Tick += (_, _) => RefreshPresentation();
        SystemEvents.UserPreferenceChanged += SystemPreferencesChanged;
        SetupTray();
        if (preferences.Bounds.Width > 0)
        {
            var screen = Screen.FromRectangle(preferences.Bounds);
            Bounds = UiPreferences.ClampBounds(preferences.Bounds, screen.WorkingArea);
            StartPosition = FormStartPosition.Manual;
        }
        if (preferences.Maximized) WindowState = FormWindowState.Maximized;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Navigate(preferences.Destination);
        refresh.Start();
        _ = InspectUpdateRecoveryAsync();
        trackingInitialization = Task.Run(() => tracker.SetFolders(trackingRoots));
        try
        {
            var settings = await Task.Run(() => ArchiveSettings.Load(dataFolder));
            await UiThread.Switch(this);
            if (!shuttingDown && settings.WatchContinuously) archive.Start(settings);
        }
        catch (Exception ex) { persistentWarning += "\nArchive could not resume: " + ex.Message; }
        await UiThread.Switch(this);
        if (preferences.LaunchIntoTray && preferences.SetupDismissed && !shuttingDown && !IsDisposed) Hide();
        RefreshPresentation();
        if (!shuttingDown && !IsDisposed) _ = RefreshWorkingSummaryAsync();
    }
    internal void ActivateExisting()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate(); BringToFront();
    }
    internal void Navigate(string name)
    {
        if (shuttingDown) return;
        heading.Text = name; heading.Refresh();
        if (name == "Archive" && !pages.ContainsKey(name))
        { archivePage = new(archive, dataFolder, jobs, preferences, () => workingFolder, () => OpenSettingsSection("Presets"), RefreshPresentation); pages[name] = archivePage; }
        if (name is "Batches" or "Find & clean" or "Rename & shuffle" && !pages.ContainsKey(name)) BuildTools(name);
        if (name == "Settings" && !pages.ContainsKey(name)) pages[name] = BuildSettings();
        if (!pages.TryGetValue(name, out var page)) { name = "Overview"; page = pages[name]; }
        content.SuspendLayout();
        foreach (Control child in content.Controls) child.Visible = false;
        if (!content.Controls.Contains(page)) { page.Dock = page is TableLayoutPanel ? DockStyle.Top : DockStyle.Fill; content.Controls.Add(page); DesktopTheme.Apply(page); }
        page.Visible = true; page.BringToFront(); content.ResumeLayout();
        selected = name; heading.Text = name;
        foreach (DesktopButton button in navigation.Controls.OfType<DesktopButton>()) button.Selected = Equals(button.Tag, name);
        if (name == "Activity") RefreshActivity();
    }
    private void BuildOverview()
    {
        var page = DesktopTheme.Stack();
        DesktopTheme.Add(page, DesktopTheme.Text("Manage your clip library with verified copies and clear file context."));
        DesktopTheme.Add(page, overview);
        DesktopTheme.Add(page, DesktopTheme.Actions(DesktopTheme.Button("Choose working folder…", ChooseWorkingFolder),
            DesktopTheme.Button("Configure Archive", () => Navigate("Archive")), DesktopTheme.Button("Duration tracking", () => OpenSettingsSection("Duration tracking"))));
        if (!preferences.SetupDismissed)
        {
            var setup = DesktopTheme.Stack();
            DesktopTheme.Add(setup, DesktopTheme.Text("First use — optional setup\n1. Choose a working folder for naming.\n2. Add duration tracking roots in Settings.\n3. Configure Archive when you want verified copies.\n4. Review startup and tray preferences in Settings."));
            DesktopTheme.Add(setup, DesktopTheme.Button("Skip setup", () => { preferences.SetupDismissed = true; setup.Visible = false; SavePreferences(); }));
            DesktopTheme.Add(page, setup);
        }
        DesktopTheme.Add(page, DesktopTheme.Actions(DesktopTheme.Button("Prepare Batch", () => OpenTool(PresetAction.PrepareBatch)),
            DesktopTheme.Button("Compare folders", () => OpenTool(PresetAction.SeekDuplications)), DesktopTheme.Button("Rename clips", () => OpenTool(PresetAction.ShuffleAndName))));
        pages["Overview"] = page;
    }
    private void BuildActivity()
    {
        var page = new Panel { Height = 580, Dock = DockStyle.Top, Padding = new Padding(16) };
        var controls = DesktopTheme.Actions(DesktopTheme.Button("Copy details", () => { if (activityDetails.Text.Length > 0) Clipboard.SetText(activityDetails.Text); }),
            DesktopTheme.Button("Open log location", () => { try { Directory.CreateDirectory(dataFolder); DesktopTheme.Open(dataFolder); } catch (Exception ex) { persistentWarning = ex.Message; } }));
        controls.Dock = DockStyle.Bottom;
        activityList.SelectedIndexChanged += (_, _) =>
        {
            if (activityList.SelectedIndex < 0) return;
            var entry = displayedActivity[activityList.SelectedIndex];
            activityDetails.Text = $"{entry.Action} — {DesktopTheme.StateLabel(entry.State)}\r\n{entry.Context}\r\nStarted: {entry.Started:g}\r\nFinished: {entry.Finished:g}\r\n\r\n{entry.Details}";
        };
        activityFilter.Items.AddRange(["All outcomes", "Needs attention", "Completed", "Cancelled / interrupted"]); activityFilter.SelectedIndex = 0;
        activityFilter.SelectedIndexChanged += (_, _) => RefreshActivity();
        page.Controls.Add(activityDetails); page.Controls.Add(activityList); page.Controls.Add(activityFilter); page.Controls.Add(controls); pages["Activity"] = page;
    }
    private void RefreshActivity()
    {
        string? selection = activityList.SelectedItem?.ToString();
        displayedActivity = history.Entries.AsEnumerable().Reverse().Where(e => activityFilter.SelectedIndex switch
        {
            1 => e.State is JobState.Failed or JobState.CompletedWithIssues or JobState.Interrupted,
            2 => e.State == JobState.Completed, 3 => e.State is JobState.Cancelled or JobState.Interrupted, _ => true
        }).ToList();
        activityList.BeginUpdate(); activityList.Items.Clear();
        foreach (var entry in displayedActivity) activityList.Items.Add($"{entry.Started:g} • {entry.Action} • {DesktopTheme.StateLabel(entry.State)}");
        if (selection is not null) activityList.SelectedIndex = activityList.Items.IndexOf(selection);
        if (activityList.SelectedIndex < 0 && activityList.Items.Count > 0) activityList.SelectedIndex = 0;
        if (activityList.Items.Count == 0) activityDetails.Text = "No matching jobs yet. Analyze or run a tool to record an outcome here.";
        activityList.EndUpdate();
    }
    private void RefreshPresentation()
    {
        if (shuttingDown || IsDisposed) return;
        var state = archive.Status;
        TrackArchiveActivity(state);
        string archiveState = state.State switch { ArchiveRuntimeState.WaitingForForegroundWork => "Waiting for foreground work", ArchiveRuntimeState.WaitingForStableFile => "Waiting for stable files", _ => state.State.ToString() };
        archiveIndicator.Text = $"Archive: {archiveState} ({state.Queued} pending)";
        var value = jobs.Progress;
        var elapsed = jobs.Started;
        jobStatus.Text = jobs.Busy ? $"{jobs.Title}: {DesktopTheme.StateLabel(jobs.State)} • {(elapsed.HasValue ? WorkflowWorkspace.Duration(DateTimeOffset.Now - elapsed.Value) : "")}\n{value.Message}" : $"{jobs.Title}: {DesktopTheme.StateLabel(jobs.State)}";
        cancel.Visible = jobs.Busy; cancel.Enabled = jobs.Busy && jobs.State != JobState.Cancelling;
        progress.Visible = jobs.Busy;
        progress.Style = value.Total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
        if (value.Total > 0) progress.Value = Math.Clamp((int)((long)value.Current * 100 / value.Total), 0, 100);
        tray.Text = ($"Clips Manager | Archive {archiveState} | Tracking {trackingRoots.Count}")[..Math.Min(63, $"Clips Manager | Archive {archiveState} | Tracking {trackingRoots.Count}".Length)];
        overview.Text = $"Working folder (naming only): {(workingFolder.Length == 0 ? "Choose a folder to get started" : workingFolder)}\n" +
            workingSummary + "\n" +
            $"Duration tracking: {trackingRoots.Count} independent roots\nArchive: {archiveState}; {state.Copied} copied and verified this run, {state.Queued} pending, {state.Failed} failed/retrying.\n" +
            $"Last verified copy: {state.LastSuccess?.ToLocalTime().ToString("g") ?? "none"}. Pending clips are not yet protected.\n" +
            state.Warning + (state.UnavailableRoots.Length > 0 ? "\nUnavailable roots: " + string.Join("; ", state.UnavailableRoots) : "") + "\n" + persistentWarning +
            (history.Entries.LastOrDefault() is { } recent ? $"\nRecent outcome: {recent.Action} — {DesktopTheme.StateLabel(recent.State)}. Open Activity for counts and recovery details." : "");
        archivePage?.RefreshStatus();
        foreach (var workspace in workflows.Values) workspace.SetBusy(jobs.Busy);
        if (tray.ContextMenuStrip is { } menu)
        {
            menu.Items[2].Enabled = state.Running && !state.Paused;
            menu.Items[3].Enabled = state.Running && state.Paused;
            menu.Items[4].Enabled = state.Running;
        }
    }
    private void TrackArchiveActivity(ArchiveStatus state)
    {
        if (state.Running && archiveActivity is null)
        {
            var settings = archive.Settings;
            archiveActivity = new("Archive", "Sources: " + string.Join("; ", settings.Sources) + "\nDestination: " + settings.Destination,
                DateTimeOffset.Now, null, JobState.Running, settings.WatchContinuously ? "Continuous watch is running. Open Archive for pending/retry details." : "Run once is running. Open Archive for pending/retry details.");
            history.Add(archiveActivity);
        }
        else if (!state.Running && archiveActivity is { } record)
        {
            var outcome = record with { Finished = DateTimeOffset.Now,
                State = state.Failed > 0 || state.Warning.Length > 0 ? JobState.CompletedWithIssues : archive.CancellationToken.IsCancellationRequested ? JobState.Cancelled : JobState.Completed,
                Details = $"Copied and verified: {state.Copied}\nAlready archived: {state.AlreadyArchived}\nFailed/retrying: {state.Failed}\nPending at stop: {state.Queued}\n{state.Warning}\nCompleted verified copies retained. Inspect Archive and archive_log.json for pending/retry details." };
            if (history.Entries.Contains(record)) history.Replace(record, outcome); else history.Add(outcome);
            archiveActivity = null;
        }
        else return;
        try { history.Save(dataFolder); } catch (Exception ex) { persistentWarning = "Archive activity could not be saved: " + ex.Message; }
        if (selected == "Activity") RefreshActivity();
    }
    private void SetupTray()
    {
        tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        tray.Visible = true; tray.Text = "Clips Manager";
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ActivateExisting(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Clips Manager", null, (_, _) => ActivateExisting());
        menu.Items.Add("Archive status", null, (_, _) => { ActivateExisting(); Navigate("Archive"); });
        menu.Items.Add("Pause Archive", null, (_, _) => archive.Pause());
        menu.Items.Add("Resume Archive", null, (_, _) => archive.Resume());
        menu.Items.Add("Stop Archive", null, async (_, _) => await StopArchiveAsync());
        menu.Items.Add(new ToolStripSeparator());
        foreach (var action in Enum.GetValues<PresetAction>().Where(a => a != PresetAction.Archive))
        {
            var captured = action; menu.Items.Add(ActionPreset.Label(action), null, (_, _) => { ActivateExisting(); OpenTool(captured); });
        }
        menu.Items.Add("Manage presets…", null, (_, _) => { ActivateExisting(); OpenSettingsSection("Presets"); });
        menu.Items.Add("Duration tracking", null, (_, _) => { ActivateExisting(); OpenSettingsSection("Duration tracking"); });
        menu.Items.Add("Settings", null, (_, _) => { ActivateExisting(); Navigate("Settings"); });
        menu.Items.Add("Check for updates", null, (_, _) => { ActivateExisting(); OpenSettingsSection("Updates"); _ = CheckUpdatesAsync(); });
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        menu.Opening += (_, _) => RefreshPresentation(); DesktopTheme.StyleMenu(menu); tray.ContextMenuStrip = menu;
    }
    private async Task StopArchiveAsync()
    {
        try
        {
            var next = archive.Settings.Clone(); next.WatchContinuously = false; next.Save(dataFolder);
            await archive.StopAsync(); await UiThread.Switch(this);
            if (!IsDisposed) { archivePage?.Apply(next); RefreshPresentation(); }
        }
        catch (Exception ex) { persistentWarning = "Archive Stop needs attention: " + ex.Message; }
    }
    private void ChooseWorkingFolder()
    {
        using var picker = new FolderBrowserDialog { Description = "Working folder for naming only", SelectedPath = workingFolder };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Directory.CreateDirectory(dataFolder);
            File.WriteAllText(Path.Combine(dataFolder, "working_folder.txt"), picker.SelectedPath);
            workingFolder = picker.SelectedPath; RememberPath(workingFolder);
        }
        catch (Exception ex) { persistentWarning = "Working folder could not be saved: " + ex.Message; }
        RefreshPresentation();
        _ = RefreshWorkingSummaryAsync();
    }
    private async Task RefreshWorkingSummaryAsync()
    {
        folderRefresh?.Cancel(); folderRefresh?.Dispose(); folderRefresh = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = folderRefresh.Token; string path = workingFolder;
        try
        {
            string summary = await Task.Run(() =>
            {
                if (path.Length == 0) return "Choose a working folder to see its clip count.";
                if (!Directory.Exists(path)) return "Working folder is unavailable; its saved path is retained.";
                int count = 0;
                foreach (string file in Directory.EnumerateFiles(path))
                {
                    token.ThrowIfCancellationRequested();
                    if (VideoExtensions.Contains(Path.GetExtension(file)) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) count++;
                }
                return $"{count:N0} top-level videos; summary refreshed {DateTime.Now:g}.";
            }, token);
            await UiThread.Switch(this);
            if (!token.IsCancellationRequested && !IsDisposed) { workingSummary = summary; RefreshPresentation(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested && !IsDisposed) workingSummary = "Folder summary could not be refreshed: " + ex.Message; }
    }
    internal void RememberPath(string path)
    {
        if (path.Length == 0) return;
        preferences.RecentPaths.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        preferences.RecentPaths.Insert(0, path); preferences.RecentPaths = preferences.RecentPaths.Take(12).ToList();
    }
    private void SavePreferences()
    {
        try
        {
            preferences.Bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            preferences.Maximized = WindowState == FormWindowState.Maximized; preferences.Destination = selected;
            preferences.Save(dataFolder);
        }
        catch (Exception ex) { persistentWarning = "UI preferences could not be saved: " + ex.Message; }
    }
    private void SystemPreferencesChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!IsHandleCreated || IsDisposed || Disposing) return;
        try { BeginInvoke(() => { if (!IsDisposed && !Disposing) DesktopTheme.Apply(this); }); } catch (InvalidOperationException) { }
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exitReady)
        {
            e.Cancel = true;
            if (e.CloseReason == CloseReason.UserClosing && preferences.CloseToTray && !shuttingDown)
            {
                SavePreferences(); Hide();
                if (!preferences.SetupDismissed) { persistentWarning = "Closing keeps Clips Manager in the tray. Use Exit to stop work; change this in Settings."; }
            }
            else _ = ExitAsync(e.CloseReason == CloseReason.WindowsShutDown);
        }
        base.OnFormClosing(e);
    }
    private async Task ExitAsync(bool bounded = false)
    {
        if (shuttingDown) return;
        shuttingDown = true; jobs.Cancel(); lifetime.Cancel();
        heading.Text = "Exiting — waiting for safe cancellation and recovery…";
        SavePreferences();
        var tasks = new List<Task> { archive.StopAsync() };
        if (jobs.ActiveTask is { } active) tasks.Add(active);
        if (trackingInitialization is { } initialization) tasks.Add(initialization);
        tasks.Add(Task.Run(() => tracker.Dispose()));
        var drain = Task.WhenAll(tasks);
        if (bounded) await Task.WhenAny(drain, Task.Delay(3000)); else await drain;
        await UiThread.Switch(this);
        if (IsDisposed) return;
        TrackArchiveActivity(archive.Status);
        try { history.Save(dataFolder); } catch (Exception ex) { persistentWarning = "History could not be saved: " + ex.Message; }
        exitReady = true; Close();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true;
            SystemEvents.UserPreferenceChanged -= SystemPreferencesChanged;
            lifetime.Cancel(); refresh.Dispose(); if (!jobs.Busy) jobs.Dispose(); tray.ContextMenuStrip?.Dispose(); tray.Dispose();
            folderRefresh?.Cancel(); folderRefresh?.Dispose(); folderRefresh = null;
            if (!archive.Running) archive.Dispose();
            lifetime.Dispose();
            if (!archive.Running && !jobs.Busy) coordinator.Dispose();
            if (trackingInitialization is null || trackingInitialization.IsCompleted) tracker.Dispose();
        }
        base.Dispose(disposing);
    }
}
