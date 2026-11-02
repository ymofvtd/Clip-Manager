namespace VideoTrayApp;

internal sealed class CompilationWorkspace : UserControl
{
    private readonly MainShell shell;
    private readonly CompilationSessions store;
    private readonly ComboBox sessions = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 350, AccessibleName = "Compilation sessions" };
    private readonly PathPicker destination, watchSource;
    private readonly TextBox selectedFiles = new() { ReadOnly = true,
        PlaceholderText = "Browse to select one or more MP4 clips.",
        AccessibleName = "Selected compilation clips" };
    private readonly TextBox folderName = new() { Width = 260, AccessibleName = "Compilation folder name" };
    private readonly DetailsBox status = new() { Dock = DockStyle.Bottom, Height = 85, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, AccessibleName = "Compilation status" };
    private readonly Label active = DesktopTheme.Text(""), total = DesktopTheme.Text("Total duration: —"), sourceStatus = DesktopTheme.Text("");
    private readonly FileContextList sourceClips = new(selection: true) { Dock = DockStyle.Fill };
    private readonly FileContextList clips = new(contextOnly: true) { Dock = DockStyle.Fill };
    private readonly DesktopButton lockIn, add, addSelected, rename, forget, watch, stopWatch;
    private readonly Panel settingsPage = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Panel sourcePage = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Panel compilationPage = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly DesktopButton settingsTab, sourceTab, compilationTab;
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 500 };
    private readonly CompilationCatalog sourceCatalog = new(), targetCatalog = new();
    private FileSystemWatcher? sourceWatcher, targetWatcher;
    private CancellationTokenSource? scanCancellation;
    private int refreshRequested = 1, watcherFailed;
    private long lastSignal;
    private DateTime nextWatchRetry;
    private string? watchedSource, watchedTarget;
    private bool updating, draft, busy, scanning;
    private int revision;
    private CompilationSession? displayedSession;
    private bool displayedDraft;

    internal CompilationWorkspace(MainShell shell, UiPreferences preferences)
    {
        this.shell = shell; store = shell.Compilations;
        Font = DesktopTheme.BodyFont; Dock = DockStyle.Fill; AllowDrop = true;
        destination = new("Compilation folder", recent: preferences.RecentPaths);
        watchSource = new("Watched source folder (MP4 clips directly inside this folder)", recent: preferences.RecentPaths);
        var fields = DesktopTheme.Stack(); fields.Padding = new Padding(24, 12, 24, 12);
        DesktopTheme.Add(fields, DesktopTheme.Text("Choose a compilation folder, browse for MP4 clips, then add them. Clips are copied and verified; originals stay in place. A watched source folder is optional."));
        DesktopTheme.Add(fields, destination);
        lockIn = DesktopTheme.Button("Lock in folder", LockIn); lockIn.Tag = CommandRole.Primary;
        rename = DesktopTheme.Button("Rename folder", async () => await RenameAsync());
        forget = DesktopTheme.Button("Forget session", Forget);
        DesktopTheme.Add(fields, DesktopTheme.Actions(lockIn, DesktopTheme.Text("Folder name"), folderName, rename, forget));
        DesktopTheme.Add(fields, DesktopTheme.Text("Clips"));
        add = DesktopTheme.Button("Browse clips…", async () =>
        {
            if (draft || store.Active is null || busy || shell.IsBusy) return;
            using var picker = new OpenFileDialog { Title = "Select clips for " + store.Active.Name,
                Filter = VideoFiles.PickerFilter, Multiselect = true, CheckFileExists = true };
            if (picker.ShowDialog(shell) != DialogResult.OK) return;
            selectedFiles.Text = string.Join("; ", picker.FileNames);
            await AddAsync(picker.FileNames);
        });
        var clipPicker = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 };
        clipPicker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        clipPicker.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        selectedFiles.Dock = DockStyle.Fill;
        clipPicker.Controls.Add(selectedFiles, 0, 0); clipPicker.Controls.Add(add, 1, 0);
        DesktopTheme.Add(fields, clipPicker);
        DesktopTheme.Add(fields, DesktopTheme.Text("Optional watched source"));
        DesktopTheme.Add(fields, watchSource);
        watch = DesktopTheme.Button("Watch this folder", () => Try(() =>
        {
            if (busy || shell.IsBusy || store.Active is not { } session) return;
            store.WatchSource(session.Id, watchSource.Value); shell.RememberPath(watchSource.Value);
            status.Text = "Source saved. Choose clips in the Watched clips tab, then Add selected clips.";
            ShowPage(sourcePage);
        }));
        stopWatch = DesktopTheme.Button("Stop watching", () => Try(() =>
        {
            if (busy || shell.IsBusy || store.Active is not { } session) return;
            store.WatchSource(session.Id, "");
        }));
        DesktopTheme.Add(fields, DesktopTheme.Actions(watch, stopWatch));
        settingsPage.Controls.Add(fields);
        addSelected = DesktopTheme.Button("Add selected clips", async () => await AddSelectedAsync()); addSelected.Tag = CommandRole.Primary;
        var sourceActions = DesktopTheme.Actions(DesktopTheme.Button("Select all visible", sourceClips.SelectVisible),
            DesktopTheme.Button("Clear selection", sourceClips.ClearSelection), addSelected);
        var sourceHeader = DesktopTheme.Stack(); sourceHeader.Padding = new Padding(20, 0, 16, 0);
        DesktopTheme.Add(sourceHeader, sourceStatus); DesktopTheme.Add(sourceHeader, sourceActions);
        sourcePage.Controls.Add(sourceClips); sourcePage.Controls.Add(sourceHeader);
        var targetActions = DesktopTheme.Actions(DesktopTheme.Button("Open compilation folder", () => Try(() =>
            { if (!draft && store.Active is { } s) DesktopTheme.Open(s.Folder); })),
            DesktopTheme.Button("Refresh clips and duration", async () => await RefreshCatalogsAsync()));
        targetActions.Padding = new Padding(20, 0, 0, 0);
        compilationPage.Controls.Add(clips); compilationPage.Controls.Add(targetActions);
        foreach (int column in new[] { 0, 1, 4, 7 }) clips.Table.Columns[column].Visible = false;
        foreach (int column in new[] { 0, 4, 7 }) sourceClips.Table.Columns[column].Visible = false;
        sourceClips.Table.AccessibleName = "Watched compilation source clips";
        clips.Table.AccessibleName = "Compilation contents";
        sourceClips.MinimumSize = clips.MinimumSize = new Size(0, 220);
        clips.SetContextSummary("Compilation clips in filename order.");
        settingsTab = DesktopTheme.Button("Session settings", () => ShowPage(settingsPage));
        sourceTab = DesktopTheme.Button("Watched clips", () => ShowPage(sourcePage));
        compilationTab = DesktopTheme.Button("Compilation", () => ShowPage(compilationPage));
        settingsTab.Kind = sourceTab.Kind = compilationTab.Kind = ButtonKind.Tab;
        var header = DesktopTheme.Stack(); header.Padding = new Padding(20, 4, 16, 0);
        DesktopTheme.Add(header, DesktopTheme.Actions(DesktopTheme.Text("Session"), sessions, DesktopTheme.Button("New session", NewSession)));
        active.Tag = TextRole.Secondary; total.Font = DesktopTheme.EmphasisFont; total.AccessibleName = "Compilation total duration";
        DesktopTheme.Add(header, DesktopTheme.Actions(active, total));
        DesktopTheme.Add(header, DesktopTheme.Actions(settingsTab, sourceTab, compilationTab));
        var pages = new Panel { Dock = DockStyle.Fill };
        pages.Controls.Add(settingsPage); pages.Controls.Add(sourcePage); pages.Controls.Add(compilationPage);
        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        body.Controls.Add(pages); body.Controls.Add(header);
        Controls.Add(body); Controls.Add(status);
        SizeChanged += (_, _) =>
        {
            int minimum = ClientSize.Height < 500 ? 620 : 0;
            if (body.AutoScrollMinSize.Height != minimum) body.AutoScrollMinSize = new Size(0, minimum);
        };
        sourcePage.SizeChanged += (_, _) => FitList(sourcePage, sourceClips, sourceHeader.Height);
        compilationPage.SizeChanged += (_, _) => FitList(compilationPage, clips, targetActions.Height);
        sessions.SelectedIndexChanged += (_, _) =>
        {
            if (updating || busy || shell.IsBusy || sessions.SelectedItem is not CompilationSession session) return;
            Try(() => { draft = false; store.Select(session.Id); });
        };
        sourceClips.SelectionChanged += RefreshEnabled;
        store.Changed += StoreChanged;
        VisibleChanged += (_, _) => { if (Visible && !busy) { RefreshSessions(); RequestRefresh(); } };
        DragEnter += (_, e) => { if (!draft && store.Active is not null && e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) => { if (!draft && e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await AddAsync(paths); };
        refresh.Tick += async (_, _) =>
        {
            if (!Visible || busy || shell.IsBusy || scanning || draft) return;
            if (Interlocked.Exchange(ref watcherFailed, 0) != 0) StopWatchers();
            if (DateTime.UtcNow >= nextWatchRetry) ConfigureWatchers();
            if (Volatile.Read(ref refreshRequested) != 0 && DateTime.UtcNow.Ticks - Interlocked.Read(ref lastSignal) >= TimeSpan.TicksPerMillisecond * 400)
                await RefreshCatalogsAsync();
        };
        RefreshSessions(); ShowPage(settingsPage); refresh.Start();
    }

    private static void FitList(Panel page, FileContextList list, int headerHeight)
    {
        list.Dock = page.ClientSize.Height - headerHeight < 220 ? DockStyle.Top : DockStyle.Fill;
        if (list.Dock == DockStyle.Top) list.Height = 340;
    }

    private void ShowPage(Panel page)
    {
        foreach (var candidate in new[] { settingsPage, sourcePage, compilationPage }) candidate.Visible = candidate == page;
        page.BringToFront(); settingsTab.Selected = page == settingsPage; sourceTab.Selected = page == sourcePage;
        compilationTab.Selected = page == compilationPage;
    }

    private void RequestRefresh()
    { Interlocked.Exchange(ref refreshRequested, 1); Interlocked.Exchange(ref lastSignal, DateTime.UtcNow.Ticks); }

    private void StoreChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { if (InvokeRequired) BeginInvoke((Action)RefreshSessions); else RefreshSessions(); }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
    }

    private void NewSession()
    {
        if (busy || shell.IsBusy) return;
        draft = true; destination.Value = ""; folderName.Text = ""; watchSource.Value = "";
        RefreshSessions(); ShowPage(settingsPage);
        status.Text = "Choose a separate folder, then Lock in folder to make it the active session.";
    }

    private void LockIn() => Try(() =>
    {
        if (busy || shell.IsBusy) return;
        draft = false;
        try { var session = store.LockIn(destination.Value); shell.RememberPath(session.Folder); }
        catch { draft = store.Active is not null; throw; }
        RefreshSessions();
        status.Text = "Folder locked in. Browse clips to select and add files. Watching a source folder is optional.";
    });

    private void Forget() => Try(() =>
    {
        if (busy || shell.IsBusy || store.Active is not { } session) return;
        store.Forget(session.Id); draft = false; RefreshSessions();
        status.Text = "Session forgotten. Its folder and clips are retained.";
    });

    internal async Task RenameAsync()
    {
        if (busy || shell.IsBusy || store.Active is not { } session) return;
        string name = folderName.Text;
        busy = true; RefreshEnabled(); StopWatchers();
        try
        {
            var outcome = await shell.RunJobAsync("Rename compilation folder", session.Folder + "\nNew name: " + name, false,
                (_, ct) => JobOutcome.Complete("Compilation folder renamed: " + store.Rename(session.Id, name, ct).Folder));
            await UiThread.Switch(this);
            if (!IsDisposed && outcome is not null) { status.Text = outcome.Details; RefreshSessions(); }
        }
        finally { if (!IsDisposed) { busy = false; RefreshEnabled(); ConfigureWatchers(); RequestRefresh(); } }
    }

    internal Task AddSelectedAsync() => AddAsync(sourceClips.Rows.Where(r => sourceClips.SelectedKeys.Contains(r.Key)).Select(r => r.Path).ToArray());

    internal async Task AddAsync(IEnumerable<string> paths)
    {
        if (busy || shell.IsBusy || draft) return;
        busy = true; RefreshEnabled();
        try
        {
            var outcome = await shell.AddToCompilationAsync(paths);
            await UiThread.Switch(this);
            if (!IsDisposed)
            {
                status.Text = outcome?.Details ?? "Another operation is running. Try adding the clips again when it finishes.";
                if (outcome?.State == JobState.Completed) sourceClips.ClearSelection();
            }
        }
        finally { if (!IsDisposed) { busy = false; RefreshEnabled(); RequestRefresh(); } }
        if (!IsDisposed) await RefreshCatalogsAsync();
    }

    private void RefreshSessions()
    {
        if (IsDisposed) return;
        bool changed = displayedSession != store.Active || displayedDraft != draft;
        if (changed) { revision++; scanCancellation?.Cancel(); }
        updating = true;
        try
        {
            sessions.Items.Clear(); sessions.Items.AddRange(store.Sessions.Cast<object>().ToArray());
            sessions.SelectedItem = draft ? null : store.Active;
            if (changed && !draft && store.Active is { } session)
            { destination.Value = session.Folder; folderName.Text = session.Name; watchSource.Value = session.SourceFolder; }
            active.Text = !draft && store.Active is { } current ? "Active: " + current.Name + "   " : "Choose a compilation folder and lock it in.";
            active.AccessibleDescription = store.Active?.Folder;
            sourceStatus.Text = !draft && store.Active is { SourceFolder.Length: > 0 } s
                ? "Watching: " + s.SourceFolder + "\nSelect clips to copy. New arrivals appear automatically; subfolders are excluded."
                : "Set a watched source folder in Session settings to choose clips here.";
            if (changed)
            {
                selectedFiles.Clear();
                total.Text = !draft && store.Active is not null ? "Total duration: calculating…" : "Total duration: —";
                clips.SetRows([]); sourceClips.SetRows([]);
            }
            displayedSession = store.Active; displayedDraft = draft;
            if (store.Warning.Length > 0) status.Text = store.Warning;
            RefreshEnabled(); ConfigureWatchers(); RequestRefresh();
        }
        finally { updating = false; }
    }

    private void RefreshEnabled()
    {
        bool editable = !busy && store.Warning.Length == 0;
        bool locked = !draft && store.Active is not null;
        destination.Enabled = editable; destination.ReadOnly = locked;
        lockIn.Enabled = editable && !locked;
        add.Enabled = rename.Enabled = forget.Enabled = folderName.Enabled = watch.Enabled = stopWatch.Enabled = watchSource.Enabled = editable && locked;
        addSelected.Enabled = editable && locked && sourceClips.SelectedKeys.Count > 0;
        sessions.Enabled = editable;
    }

    private void ConfigureWatchers()
    {
        nextWatchRetry = DateTime.UtcNow.AddSeconds(5);
        string? source = !draft ? store.Active?.SourceFolder : null, target = !draft ? store.Active?.Folder : null;
        if (source != watchedSource || target != watchedTarget) StopWatchers();
        watchedSource = source; watchedTarget = target;
        if (sourceWatcher is null && !string.IsNullOrEmpty(source)) sourceWatcher = Watch(source);
        if (targetWatcher is null && !string.IsNullOrEmpty(target)) targetWatcher = Watch(target);
    }

    private FileSystemWatcher? Watch(string folder)
    {
        try
        {
            ActionPreset.ValidateFolder(folder); ArchivePaths.RejectLinks(folder);
            var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            watcher.Created += (_, _) => RequestRefresh(); watcher.Changed += (_, _) => RequestRefresh();
            watcher.Deleted += (_, _) => RequestRefresh(); watcher.Renamed += (_, _) => RequestRefresh();
            watcher.Error += (_, _) => { Interlocked.Exchange(ref watcherFailed, 1); RequestRefresh(); };
            watcher.EnableRaisingEvents = true; RequestRefresh();
            return watcher;
        }
        catch (Exception ex) { status.Text = "Folder watch unavailable; retrying automatically: " + folder + "\n" + ex.Message; return null; }
    }

    private void StopWatchers()
    { sourceWatcher?.Dispose(); sourceWatcher = null; targetWatcher?.Dispose(); targetWatcher = null; watchedSource = watchedTarget = null; }

    internal async Task RefreshCatalogsAsync()
    {
        if (scanning || busy || shell.IsBusy || draft || store.Active is not { } session) return;
        scanning = true; Interlocked.Exchange(ref refreshRequested, 0);
        scanCancellation?.Dispose(); scanCancellation = new();
        var ct = scanCancellation.Token; int scannedRevision = revision;
        try
        {
            var catalogs = await Task.Run(() => (Source: ReadCatalog(sourceCatalog, session.SourceFolder, ct),
                Target: ReadCatalog(targetCatalog, session.Folder, ct)), ct);
            await UiThread.Switch(this);
            if (IsDisposed || scannedRevision != revision) return;
            sourceClips.SetRows(Rows(catalogs.Source, true)); clips.SetRows(Rows(catalogs.Target, false));
            total.Text = "Total duration: " + CompilationCatalog.FormatDuration(catalogs.Target.TotalDuration)
                + $" • {catalogs.Target.Clips.Count:N0} clips" + (catalogs.Target.Errors.Count > 0 ? " (partial — some clips could not be read)" : "");
            var errors = catalogs.Source.Errors.Concat(catalogs.Target.Errors).ToArray();
            if (errors.Length > 0) status.Text = "Folder scan needs attention:\n" + string.Join("\n", errors.Take(12));
            RefreshEnabled();
        }
        catch (OperationCanceledException) { RequestRefresh(); }
        catch (Exception ex) { if (!IsDisposed) status.Text = ex.Message; }
        finally { scanning = false; }
    }

    private static CompilationCatalogResult ReadCatalog(CompilationCatalog catalog, string folder, CancellationToken ct)
    {
        if (folder.Length == 0) return new([], []);
        try { return catalog.Read(folder, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new([], [folder + ": " + ex.Message]); }
    }

    private static ContextRow[] Rows(CompilationCatalogResult catalog, bool selectable) => catalog.Clips.Select(c =>
        new ContextRow(c.File.Path, "", "", c.File.Path, "", Path.GetDirectoryName(c.File.Path)!,
            c.Duration is { } d ? CompilationCatalog.FormatDuration(d) : "Unreadable", WorkflowWorkspace.FormatSize(c.File.Length),
            Selectable: selectable && c.Duration is not null, Bytes: c.File.Length, Modified: c.File.LastWrite,
            Identity: c.File.Physical + " / " + c.File.Stamp)).ToArray();

    private void Try(Action action) { try { action(); } catch (Exception ex) { status.Text = ex.Message; } }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        { store.Changed -= StoreChanged; refresh.Stop(); refresh.Dispose(); scanCancellation?.Cancel(); StopWatchers(); }
        base.Dispose(disposing);
    }
}
