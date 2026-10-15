namespace VideoTrayApp;

internal sealed class ClipDateWorkspace : UserControl
{
    private readonly MainShell shell;
    private readonly PathPicker source;
    private readonly DateTimePicker from = new() { Format = DateTimePickerFormat.Short, Width = 145, Value = DateTime.Today, AccessibleName = "Created from date" };
    private readonly DateTimePicker through = new() { Format = DateTimePickerFormat.Short, Width = 145, Value = DateTime.Today, AccessibleName = "Created through date" };
    private readonly CheckBox recursive = new() { Text = "Include subfolders", AutoSize = true, Checked = true };
    private readonly CheckBox includeUnknown = new() { Text = "Also show clips with no embedded creation date", AutoSize = true };
    private readonly FileContextList results = new(lookup: true) { Dock = DockStyle.Fill };
    private readonly DetailsBox status = new() { Dock = DockStyle.Bottom, Height = 80, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, AccessibleName = "Date lookup status", Text = "Choose a folder and date range, then Find clips." };
    private readonly Button find;
    private readonly DesktopButton move, delete;
    private IReadOnlyList<DatedClip> matches = [];
    private bool actionBusy;
    private string searchContext = "";
    private const string Recovery = "Selected files and exact move destinations are verified again before running. Cancellation or failure attempts in-process rollback; forced termination is outside that guarantee. Successful deletion uses Windows Recycle Bin.";
    private readonly Panel configuration = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Panel resultPage = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly DesktopButton configureTab, resultsTab;
    private int revision;

    internal ClipDateWorkspace(MainShell shell, Func<string> workingFolder, UiPreferences preferences)
    {
        this.shell = shell;
        Font = DesktopTheme.BodyFont; Dock = DockStyle.Fill;
        source = new("Search folder", recent: preferences.RecentPaths) { Value = workingFolder() };
        var fields = DesktopTheme.Stack(); fields.Padding = new Padding(24, 12, 24, 12);
        DesktopTheme.Add(fields, DesktopTheme.Text("Find by date. Uses the creation date stored inside the MP4, preserved by unchanged copies and renames. Dates use your local time zone. Some recordings have no embedded date; editing or exporting can replace it."));
        DesktopTheme.Add(fields, source);
        DesktopTheme.Add(fields, DesktopTheme.Actions(DesktopTheme.Text("From"), from, DesktopTheme.Text("Through (inclusive)"), through));
        find = DesktopTheme.Button("Find clips", async () => await FindAsync()); find.Tag = CommandRole.Primary;
        DesktopTheme.Add(fields, DesktopTheme.Actions(recursive, includeUnknown, find));
        configuration.Controls.Add(fields);
        move = DesktopTheme.Button("Move selected…", async () => await ChooseMoveAsync());
        delete = DesktopTheme.Button("Delete selected…", async () => await ActAsync(null)); delete.Tag = CommandRole.Recycle;
        var actions = DesktopTheme.Actions(DesktopTheme.Button("Select all visible", results.SelectVisible),
            DesktopTheme.Button("Clear selection", results.ClearSelection), move, delete);
        actions.Padding = new Padding(20, 6, 16, 6);
        actions.AccessibleName = "Actions for selected date lookup clips";
        resultPage.Controls.Add(results);
        resultPage.Controls.Add(actions);
        resultPage.SizeChanged += (_, _) =>
        {
            results.Dock = resultPage.ClientSize.Height - actions.Height < 320 ? DockStyle.Top : DockStyle.Fill;
            if (results.Dock == DockStyle.Top) results.Height = 390;
        };
        var stages = new Panel { Dock = DockStyle.Fill };
        stages.Controls.Add(configuration); stages.Controls.Add(resultPage);
        configureTab = DesktopTheme.Button("Search settings", () => ShowResults(false));
        resultsTab = DesktopTheme.Button("Results", () => ShowResults(true));
        configureTab.Kind = resultsTab.Kind = ButtonKind.Tab;
        var tabs = DesktopTheme.Actions(configureTab, resultsTab); tabs.Padding = new Padding(20, 12, 0, 0);
        Controls.Add(stages); Controls.Add(status); Controls.Add(tabs);
        ShowResults(false);
        source.ValueChanged += (_, _) => Changed();
        from.ValueChanged += (_, _) => Changed(); through.ValueChanged += (_, _) => Changed();
        recursive.CheckedChanged += (_, _) => Changed(); includeUnknown.CheckedChanged += (_, _) => Changed();
        results.SelectionChanged += RefreshActions;
        RefreshActions();
    }

    private void Changed()
    { revision++; status.Text = "Settings changed. Find clips again to refresh the results."; }

    private void ShowResults(bool show)
    {
        configuration.Visible = !show; resultPage.Visible = show;
        if (show) resultPage.BringToFront(); else configuration.BringToFront();
        configureTab.Selected = !show; resultsTab.Selected = show;
    }

    private void RefreshActions()
    {
        move.Enabled = delete.Enabled = !actionBusy && results.SelectedKeys.Count > 0;
        find.Enabled = !actionBusy;
    }

    private async Task ChooseMoveAsync()
    {
        if (shell.IsBusy || actionBusy || results.SelectedKeys.Count == 0) return;
        using var picker = new FolderBrowserDialog { Description = "Choose the folder to isolate selected clips in", UseDescriptionForTitle = true };
        if (picker.ShowDialog(shell) == DialogResult.OK) await ActAsync(picker.SelectedPath);
    }

    internal async Task ActAsync(string? destination)
    {
        if (shell.IsBusy || actionBusy) return;
        var selected = matches.Where(c => results.SelectedKeys.Contains(c.Path)).ToArray();
        if (selected.Length == 0) { status.Text = "Select clips in the results first, or use Select all visible."; return; }
        string title = destination is null ? "Delete selected date matches" : "Move selected date matches";
        string context = searchContext + $"\nSelected: {selected.Length:N0} clips (including selected rows hidden by filters)."
            + (destination is null ? "\nAction: Send to Windows Recycle Bin" : "\nMove destination: " + destination);
        if (shell.Archive.Running && selected.Any(c => ArchivePaths.Contains(shell.Archive.Settings.Destination, c.Path)))
            context += "\nArchive is watching: removing these archive copies can cause them to be archived again from surviving watched originals.";
        actionBusy = true; RefreshActions();
        try
        {
            ClipDateActionPlan? plan = null;
            var prepared = await shell.RunJobAsync(title + " — prepare review", context, true, (progress, ct) =>
            {
                plan = ClipDateActionPlan.Create(selected, destination, progress, ct);
                return new(JobState.ReadyToReview, $"{plan.Changes.Count:N0} selected clips ready for exact-file confirmation.");
            });
            await UiThread.Switch(this);
            if (IsDisposed || prepared is null) return;
            if (prepared.State != JobState.ReadyToReview || plan is null) { status.Text = prepared.Details; return; }
            using var confirmation = new ReviewConfirmation(title, context, plan.ReviewRows(), Recovery);
            if (confirmation.ShowDialog(shell) != DialogResult.OK) { status.Text = "Action cancelled before making changes. Selection retained."; return; }
            var outcome = await shell.RunJobAsync(title, context, false,
                (progress, ct) => JobOutcome.Complete(plan.Execute(progress, ct)));
            await UiThread.Switch(this);
            if (IsDisposed || outcome is null) return;
            if (outcome.State == JobState.Completed)
            {
                var acted = selected.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                matches = matches.Where(c => !acted.Contains(c.Path)).ToArray();
                results.SetRows(results.Rows.Where(r => !acted.Contains(r.Key)).ToArray());
                if (destination is not null) shell.RememberPath(destination);
            }
            status.Text = outcome.Details + (outcome.State == JobState.Completed ? "" : "\nFind clips again before retrying to refresh file metadata and paths.");
        }
        finally { if (!IsDisposed) { actionBusy = false; RefreshActions(); } }
    }

    internal async Task FindAsync()
    {
        if (shell.IsBusy || actionBusy) return;
        var first = DateOnly.FromDateTime(from.Value); var last = DateOnly.FromDateTime(through.Value);
        if (first > last) { status.Text = "From date must be on or before Through date."; return; }
        string folder = source.Value;
        bool recurse = recursive.Checked, unknown = includeUnknown.Checked;
        int searchedRevision = revision;
        ClipDateResult? scan = null;
        matches = []; results.SetRows([]); find.Enabled = false; move.Enabled = delete.Enabled = false;
        try
        {
            searchContext = $"Search folder: {folder}\nCreated: {first:yyyy-MM-dd} through {last:yyyy-MM-dd} (inclusive, {TimeZoneInfo.Local.DisplayName})\nInclude subfolders: {recurse}; include unknown dates: {unknown}";
            var outcome = await shell.RunJobAsync("Find by date", searchContext, true,
                (progress, ct) =>
                {
                    scan = ClipDateLookup.Find(folder, first, last, recurse, unknown, TimeZoneInfo.Local, progress, ct);
                    string summary = $"{scan.Matches.Count:N0} results from {scan.Scanned:N0} MP4 clips. Missing/unsupported creation dates: {scan.Unknown:N0}. Read/scan errors: {scan.Errors.Count:N0}."
                        + (scan.Errors.Count > 0 ? "\nIncomplete scan:\n" + string.Join("\n", scan.Errors.Take(20)) : "");
                    return new(scan.Errors.Count > 0 ? JobState.CompletedWithIssues : JobState.Completed, summary);
                });
            await UiThread.Switch(this);
            if (IsDisposed || outcome is null) return;
            if (scan is not null)
            {
                matches = scan.Matches;
                results.SetRows(scan.Matches.Select(c => new ContextRow(c.Path, "", "", c.Path,
                    c.Created is null ? "No embedded date" : "Embedded MP4 movie creation date", Path.GetDirectoryName(c.Path)!, "",
                    WorkflowWorkspace.FormatSize(c.Size), Selectable: true, Bytes: c.Size, Created: c.Created)).ToArray());
                shell.RememberPath(folder);
            }
            status.Text = outcome.Details + (searchedRevision != revision ? "\nSettings changed during search. Find clips again to refresh." : "");
            ShowResults(true);
        }
        finally { if (!IsDisposed) RefreshActions(); }
    }
}
