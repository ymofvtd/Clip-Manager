namespace VideoTrayApp;

internal sealed class ArchiveWorkspace : UserControl
{
    private readonly ArchiveWorker worker;
    private readonly string dataFolder;
    private readonly JobController jobs;
    private readonly Action stateChanged;
    private readonly SourceFolderList sources = new();
    private readonly PathPicker destination;
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, AccessibleName = "Archive run mode" };
    private readonly ComboBox speed = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, AccessibleName = "Archive speed" };
    private readonly NumericUpDown interval = new() { Minimum = 0, Maximum = 86400, Value = 120, Width = 100, AccessibleName = "Seconds between clip starts" };
    private readonly NumericUpDown settling = new() { Minimum = 0, Maximum = 3600, Value = 10, Width = 100, AccessibleName = "Settle seconds" };
    private readonly NumericUpDown bandwidth = new() { Minimum = 1, Maximum = 10000, Value = 20, Width = 100, AccessibleName = "Aggregate megabytes per second" };
    private readonly CheckBox limit = new() { Text = "Limit aggregate content I/O (decimal MB/s)", Checked = true, AutoSize = true };
    private readonly Label status = DesktopTheme.Text("");
    private readonly Label warning = DesktopTheme.Text("");
    private readonly DetailsBox log = new() { Multiline = true, ReadOnly = true, Height = 160, ScrollBars = ScrollBars.Vertical };
    private readonly FileContextList pending = new();
    private readonly FileContextList catalog = new();
    private readonly Button start, pause, resume, stop;
    private readonly TableLayoutPanel config;
    private int reconcileSeconds = 300;
    private bool loadingCatalog;
    private ArchiveIndex? loadedCatalog;
    private int catalogPage;
    private const int CatalogPageSize = 1000;
    private readonly Label catalogPageStatus = DesktopTheme.Text("Catalog has not been loaded.");
    private readonly Label capacity = DesktopTheme.Text("Destination capacity has not been checked.");
    private readonly Button previousPage, nextPage;

    internal ArchiveWorkspace(ArchiveWorker worker, string dataFolder, JobController jobs, UiPreferences preferences, Func<string> workingFolder, Action managePresets, Action stateChanged)
    {
        this.worker = worker; this.dataFolder = dataFolder; this.jobs = jobs;
        this.stateChanged = stateChanged;
        Font = DesktopTheme.BodyFont; Dock = DockStyle.Fill; SuspendLayout();
        destination = new("Archive destination — independent of the working folder", recent: preferences.RecentPaths);
        mode.Items.AddRange(["Run once", "Watch continuously"]); mode.SelectedIndex = 0;
        speed.Items.AddRange(["Fast", "Slow"]); speed.SelectedIndex = 1;
        var page = DesktopTheme.Stack(); page.SuspendLayout();
        DesktopTheme.Add(page, DesktopTheme.Text("Copy and verify clips. Sources and existing archive filenames stay untouched. Completed verified copies survive cancellation."));
        var presets = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280, AccessibleName = "Archive preset" };
        var saved = new List<ActionPreset>();
        void Reload()
        {
            try
            {
                saved = PresetStore.Load(Path.Combine(dataFolder, "presets.json")).Presets.Where(p => p.Action == PresetAction.Archive).ToList();
                presets.Items.Clear(); presets.Items.Add("Current configuration"); presets.Items.AddRange(saved.Select(p => (object)p.Name).ToArray()); presets.SelectedIndex = 0;
            }
            catch (Exception ex) { warning.Text = "Presets could not be read: " + ex.Message; }
        }
        presets.DropDown += (_, _) => Reload(); Reload();
        DesktopTheme.Add(page, DesktopTheme.Actions(DesktopTheme.Text("Preset"), presets, DesktopTheme.Button("Apply preset", () =>
        {
            if (presets.SelectedIndex > 0) { Apply(saved[presets.SelectedIndex - 1].ArchiveConfiguration(workingFolder())); warning.Text = "Preset applied to the next run. Archive presets run once."; }
        }), DesktopTheme.Button("Save preset…", () =>
        {
            try
            {
                var preset = new ActionPreset { Action = PresetAction.Archive, UseWorkingFolder = false, ArchiveSources = sources.Paths,
                    Destination = destination.Value, ArchiveSpeed = speed.SelectedIndex == 0 ? ArchiveSpeed.Fast : ArchiveSpeed.Slow,
                    ArchiveIntervalSeconds = (int)interval.Value, ArchiveSettlingSeconds = (int)settling.Value, ArchiveMegabytesPerSecond = limit.Checked ? (int)bandwidth.Value : null };
                using var editor = new PresetEditorForm(preset, workingFolder());
                if (editor.ShowDialog(FindForm()) != DialogResult.OK) return;
                var store = PresetStore.Load(Path.Combine(dataFolder, "presets.json")); store.Presets.Add(editor.Preset); store.Save(Path.Combine(dataFolder, "presets.json")); Reload();
            }
            catch (Exception ex) { warning.Text = "Preset could not be saved: " + ex.Message; }
        }), DesktopTheme.Button("Manage presets…", managePresets)));
        config = DesktopTheme.Stack(); config.Padding = new Padding(0);
        DesktopTheme.Add(config, sources); DesktopTheme.Add(config, destination);
        DesktopTheme.Add(config, DesktopTheme.Actions(DesktopTheme.Button("Check destination capacity", async () => await CheckCapacityAsync()), capacity));
        DesktopTheme.Add(config, DesktopTheme.Actions(DesktopTheme.Text("Mode"), mode, DesktopTheme.Text("Speed"), speed));
        var advanced = DesktopTheme.Stack(); advanced.Padding = new Padding(0); advanced.Visible = false;
        DesktopTheme.Add(advanced, DesktopTheme.Actions(DesktopTheme.Text("Seconds between clip starts (Slow)"), interval,
            DesktopTheme.Text("Settle seconds"), settling));
        DesktopTheme.Add(advanced, DesktopTheme.Actions(limit, bandwidth));
        DesktopTheme.Add(config, DesktopTheme.Button("Advanced scheduling and I/O…", () => advanced.Visible = !advanced.Visible));
        DesktopTheme.Add(config, advanced);
        DesktopTheme.Add(config, DesktopTheme.Text("Slow defaults: one clip per 120 seconds; 20 MB/s aggregate reads/writes/verification; 10-second settling. Fast has no application I/O cap."));
        DesktopTheme.Add(page, config);
        start = DesktopTheme.Button("Start", Start);
        pause = DesktopTheme.Button("Pause", () => worker.Pause());
        resume = DesktopTheme.Button("Resume", () => worker.Resume());
        stop = DesktopTheme.Button("Stop", async () => await StopAsync());
        var commands = DesktopTheme.Actions(start, pause, resume, stop,
            DesktopTheme.Button("Retry / reconcile", () => worker.RequestReconciliation())); commands.Dock = DockStyle.Bottom; commands.Padding = new Padding(8);
        DesktopTheme.Add(page, DesktopTheme.Text("Pause retains watching. Stop disables watch auto-resume. Configuration edits apply to the next run. Navigating away keeps Archive running."));
        DesktopTheme.Add(page, warning); DesktopTheme.Add(page, status);
        DesktopTheme.Add(page, DesktopTheme.Text("Pending files and retry issues (bounded to 4,096 paths)")); DesktopTheme.Add(page, pending);
        DesktopTheme.Add(page, DesktopTheme.Text("Archive log — the latest 200 events")); DesktopTheme.Add(page, log);
        DesktopTheme.Add(page, DesktopTheme.Button("Load archive details", async () => await LoadCatalogAsync()));
        DesktopTheme.Add(page, DesktopTheme.Text("Catalog entries describe previously indexed copies, not a fresh whole-library integrity check. Use Start to reconcile and verify content."));
        previousPage = DesktopTheme.Button("Previous catalog page", () => { catalogPage--; ShowCatalogPage(); });
        nextPage = DesktopTheme.Button("Next catalog page", () => { catalogPage++; ShowCatalogPage(); });
        previousPage.Enabled = nextPage.Enabled = false;
        DesktopTheme.Add(page, DesktopTheme.Actions(previousPage, nextPage, catalogPageStatus));
        DesktopTheme.Add(page, catalog);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; scroll.Controls.Add(page); Controls.Add(scroll); Controls.Add(commands);
        try
        {
            Apply(worker.Running ? worker.Settings : ArchiveSettings.Load(dataFolder));
            if (!worker.Running && PresetStore.Load(Path.Combine(dataFolder, "presets.json")).DefaultFor(PresetAction.Archive) is { } preset)
            { Apply(preset.ArchiveConfiguration(workingFolder())); warning.Text = "Default Archive preset filled the next-run configuration. Review before Start; presets run once."; }
        }
        catch (Exception ex) { warning.Text = "Settings need attention; saved configuration retained. " + ex.Message; }
        RefreshStatus(); page.ResumeLayout(false); ResumeLayout(false);
    }
    internal void Apply(ArchiveSettings settings)
    {
        sources.Paths = settings.Sources; destination.Value = settings.Destination; mode.SelectedIndex = settings.WatchContinuously ? 1 : 0;
        speed.SelectedIndex = settings.Speed == ArchiveSpeed.Fast ? 0 : 1;
        interval.Value = Math.Clamp(settings.IntervalSeconds, 0, 86400); settling.Value = Math.Clamp(settings.SettlingSeconds, 0, 3600);
        limit.Checked = settings.MegabytesPerSecond.HasValue; bandwidth.Value = Math.Clamp(settings.MegabytesPerSecond ?? 20, 1, 10000);
        reconcileSeconds = settings.ReconcileSeconds;
    }
    private void Start()
    {
        try
        {
            var next = new ArchiveSettings { Sources = sources.Paths, Destination = destination.Value, WatchContinuously = mode.SelectedIndex == 1,
                Speed = speed.SelectedIndex == 0 ? ArchiveSpeed.Fast : ArchiveSpeed.Slow, IntervalSeconds = (int)interval.Value,
                SettlingSeconds = (int)settling.Value, MegabytesPerSecond = limit.Checked ? (int)bandwidth.Value : null, ReconcileSeconds = reconcileSeconds };
            next.Validate(false); next.Save(dataFolder); worker.Start(next); warning.Text = ""; stateChanged();
        }
        catch (Exception ex) { warning.Text = ex.Message; }
    }
    private async Task StopAsync()
    {
        stop.Enabled = false;
        try { var next = worker.Settings.Clone(); next.WatchContinuously = false; next.Save(dataFolder); await worker.StopAsync(); await UiThread.Switch(this); if (!IsDisposed) { mode.SelectedIndex = 0; stateChanged(); } }
        catch (Exception ex) { await UiThread.Switch(this); if (!IsDisposed) warning.Text = "Stop could not be completed or saved: " + ex.Message; }
    }
    internal void RefreshStatus()
    {
        if (IsDisposed) return;
        var value = worker.Status;
        start.Enabled = !value.Running && !jobs.Busy; pause.Enabled = value.Running && !value.Paused;
        resume.Enabled = value.Running && value.Paused; stop.Enabled = value.Running;
        status.Text = $"{DesktopTheme.StateLabel(value.State)} • {value.Queued} pending • {value.Copied} copied and verified • {value.AlreadyArchived} already archived • {value.Failed} failed/retrying\n" +
            $"Last successful verification: {value.LastSuccess?.ToLocalTime().ToString("g") ?? "none"}\n{value.Current}\n{value.Warning}\n" +
            (value.UnavailableRoots.Length > 0 ? "Unavailable roots: " + string.Join("; ", value.UnavailableRoots) + "\nReconnect the drive; automatic reconciliation will retry." : "");
        string nextLog = string.Join(Environment.NewLine, value.Log);
        if (log.Text != nextLog) log.Text = nextLog;
        if (Visible) pending.SetRows(worker.Pending.Select(p => new ContextRow(p.Path, "", "Pending", p.Path,
            $"Attempt {p.Attempts}; next {p.NextAttempt.ToLocalTime():g}: {p.Reason}", Path.GetDirectoryName(p.Path)!, "", "")).ToArray(), reset: false);
    }
    private async Task LoadCatalogAsync()
    {
        if (loadingCatalog) return;
        loadingCatalog = true; string path = destination.Value; warning.Text = "Loading catalog…";
        try
        {
            var index = await Task.Run(() => ArchiveIndex.Load(ArchiveIndex.IndexPath(dataFolder, path), path));
            await UiThread.Switch(this);
            if (IsDisposed || path != destination.Value) return;
            loadedCatalog = index; catalogPage = 0; ShowCatalogPage(); warning.Text = "Catalog loaded. Only the displayed page is materialized for review.";
        }
        catch (Exception ex) { await UiThread.Switch(this); if (!IsDisposed) warning.Text = "Catalog could not be read: " + ex.Message; }
        finally { loadingCatalog = false; }
    }
    private void ShowCatalogPage()
    {
        if (loadedCatalog is null) return;
        int total = loadedCatalog.Entries.Sum(e => e.Locations.Count);
        int count = Math.Max(1, (total + CatalogPageSize - 1) / CatalogPageSize);
        catalogPage = Math.Clamp(catalogPage, 0, count - 1);
        var rows = loadedCatalog.Entries.SelectMany(e => e.Locations.Select(location => (e, location)))
            .Skip(catalogPage * CatalogPageSize).Take(CatalogPageSize).Select(item => new ContextRow(item.location, "", "Indexed", item.location,
                "Previous content identity: " + item.e.Identity.Hash, string.Join("; ", item.e.Sources.Select(s => s.RootLabel + " / " + s.OriginalName).Distinct()),
                WorkflowWorkspace.Duration(item.e.Identity.Duration), WorkflowWorkspace.FormatSize(item.e.Identity.Size), Bytes: item.e.Identity.Size)).ToArray();
        catalog.SetRows(rows); catalogPageStatus.Text = $"Page {catalogPage + 1} of {count}; {total:N0} locations; up to {CatalogPageSize:N0} per page.";
        previousPage.Enabled = catalogPage > 0; nextPage.Enabled = catalogPage + 1 < count;
    }
    private async Task CheckCapacityAsync()
    {
        string path = destination.Value; capacity.Text = "Checking capacity…";
        try
        {
            string value = await Task.Run(() =>
            {
                if (!Directory.Exists(path)) return "Destination unavailable. Saved path retained; reconnect before Start.";
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
                return $"Available: {WorkflowWorkspace.FormatSize(drive.AvailableFreeSpace)}; checked {DateTime.Now:g}. Actual copying may still fail as capacity changes.";
            });
            await UiThread.Switch(this); if (!IsDisposed && path == destination.Value) capacity.Text = value;
        }
        catch (Exception ex) { await UiThread.Switch(this); if (!IsDisposed) capacity.Text = "Capacity unknown: " + ex.Message; }
    }
}
