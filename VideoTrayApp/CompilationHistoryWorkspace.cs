namespace VideoTrayApp;

internal sealed partial class CompilationHistoryWorkspace : UserControl
{
    private readonly MainShell shell;
    private readonly bool saving;
    private readonly PathPicker source;
    private readonly TextBox name = new() { Width = 320, PlaceholderText = "Use the folder name", AccessibleName = "Compilation history name" };
    private readonly DateTimePicker publishDate = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Width = 190,
        AccessibleName = "Compilation publish date (local)" };
    private readonly CheckBox recursive = new() { Text = "Include subfolders", AutoSize = true };
    private readonly CheckBox recentOnly = new() { Text = "Show only recent clips", AutoSize = true, Checked = true };
    private readonly FileContextList results;
    private readonly DetailsBox status = new() { Dock = DockStyle.Bottom, Height = 95, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, AccessibleName = "Compilation history status" };
    private readonly DesktopButton run;
    private readonly Panel configuration = new() { Dock = DockStyle.Fill };
    private readonly Panel resultPage = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly DesktopButton settingsTab, resultsTab;
    private readonly DesktopButton? moveSelected, deleteSelected;
    private readonly FlowLayoutPanel? clipActions;
    private CompilationRecencyResult? checkedResult;
    private bool busy;

    internal CompilationHistoryWorkspace(MainShell shell, bool saving, string folder, UiPreferences preferences)
    {
        this.shell = shell; this.saving = saving;
        results = saving ? new(contextOnly: true) : new(selection: true);
        Dock = DockStyle.Fill; Font = DesktopTheme.BodyFont;
        source = new(saving ? "Finished compilation folder" : "Folder to check", recent: preferences.RecentPaths) { Value = folder };
        var fields = DesktopTheme.Stack(); fields.Padding = new Padding(24, 12, 24, 12);
        DesktopTheme.Add(fields, DesktopTheme.Text(saving
            ? "Save the individual MP4 clips with a name and publish date, including future dates. Manage saved compilations in the list. History expires 30 days after publication."
            : "Find MP4 clips used in the last 30 days or reserved for a future compilation, even after copying or renaming. Select matches to move or send to the Recycle Bin. Edited files have different hashes."));
        DesktopTheme.Add(fields, source);
        if (saving)
        {
            DesktopTheme.Add(fields, DesktopTheme.Actions(DesktopTheme.Text("Compilation name"), name));
            DesktopTheme.Add(fields, DesktopTheme.Actions(DesktopTheme.Text("Publish date (local)"), publishDate));
        }
        run = DesktopTheme.Button(saving ? "Save compilation state" : "Check clips recency", async () => await RunAsync());
        run.Tag = CommandRole.Primary;
        var actions = DesktopTheme.Actions(recursive, run);
        actions.Dock = DockStyle.Bottom; actions.Padding = new Padding(20, 6, 16, 6);
        var scrollFields = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; scrollFields.Controls.Add(fields);
        configuration.Controls.Add(scrollFields); configuration.Controls.Add(actions);
        results.Table.AccessibleName = saving ? "Saved compilation clips" : "Clip recency results";
        results.ModifiedTimeDescription = "Publish date";
        results.SetContextSummary(saving ? "No compilation saved here yet." : "No recency check yet.");
        results.Table.Columns[0].Visible = !saving; results.Table.Columns[0].HeaderText = "Recency"; results.Table.Columns[0].Width = 110;
        results.Table.Columns[5].Visible = false;
        results.Table.Columns[1].HeaderText = saving ? "State" : "Select"; results.Table.Columns[1].Width = 90;
        results.Table.Columns[2].Width = 150;
        results.Table.Columns[4].HeaderText = saving ? "Compilation" : "Last compilation"; results.Table.Columns[4].Width = 200;
        results.Table.Columns[7].HeaderText = "Publish date (local)"; results.Table.Columns[7].Width = 155;
        results.Table.Columns[6].Width = 85;
        int displayIndex = 0;
        foreach (int column in new[] { 1, 0, 2, 4, 7, 6, 3 }) results.Table.Columns[column].DisplayIndex = displayIndex++;
        resultPage.Controls.Add(results);
        if (!saving)
        {
            moveSelected = DesktopTheme.Button("Move selected…", async () => await ChooseMoveAsync());
            deleteSelected = DesktopTheme.Button("Delete selected…", async () => await ActAsync(null));
            deleteSelected.Tag = CommandRole.Recycle;
            clipActions = DesktopTheme.Actions(DesktopTheme.Button("Select all visible", results.SelectVisible),
                DesktopTheme.Button("Clear selection", results.ClearSelection), moveSelected, deleteSelected);
            clipActions.Dock = DockStyle.Bottom; clipActions.Padding = new Padding(20, 6, 16, 6);
            resultPage.Controls.Add(clipActions);
            results.SelectionChanged += RefreshEnabled;
        }
        var pages = new Panel { Dock = DockStyle.Fill }; pages.Controls.Add(configuration); pages.Controls.Add(resultPage);
        settingsTab = DesktopTheme.Button("Settings", () => ShowPage(false));
        resultsTab = DesktopTheme.Button(saving ? "Saved clips" : "Recency results", () => ShowPage(true));
        settingsTab.Kind = resultsTab.Kind = ButtonKind.Tab;
        var tabs = DesktopTheme.Actions(settingsTab, resultsTab); tabs.Padding = new Padding(20, 8, 16, 0);
        if (saving) BuildHistoryPage(pages, tabs);
        if (!saving) tabs.Controls.Add(recentOnly);
        Controls.Add(pages); Controls.Add(status); Controls.Add(tabs);
        ShowPage(false);
        source.ValueChanged += (_, _) => ClearResults();
        recursive.CheckedChanged += (_, _) => ClearResults();
        recentOnly.CheckedChanged += (_, _) => ShowResults();
        status.Text = saving
            ? "Choose the folder containing the individual clips, rather than the exported compilation video. Saving leaves your videos in place."
            : "Choose a folder to check. Matches can be selected and moved or deleted after review. No saved use means no match in the retained history.";
        VisibleChanged += async (_, _) => { if (Visible) { RefreshEnabled(); if (saving) await ReloadHistoryAsync(); } };
        SizeChanged += (_, _) => status.Height = ClientSize.Height < 400 ? 64 : 95;
        resultPage.SizeChanged += (_, _) =>
        {
            results.Dock = resultPage.ClientSize.Height < 300 ? DockStyle.Top : DockStyle.Fill;
            if (results.Dock == DockStyle.Top) results.Height = 390;
        };
    }

    private void ShowPage(bool showResults)
    {
        configuration.Visible = !showResults; resultPage.Visible = showResults;
        if (historyPage is not null) historyPage.Visible = false;
        if (historyTab is not null) historyTab.Selected = false;
        (showResults ? resultPage : configuration).BringToFront();
        settingsTab.Selected = !showResults; resultsTab.Selected = showResults;
        recentOnly.Visible = !saving && showResults;
    }

    private void ClearResults()
    {
        checkedResult = null; results.SetRows([]);
        results.SetContextSummary("Folder or options changed. Run the action to refresh results.");
        ShowPage(false);
    }

    internal void SetFolder(string folder)
    { source.Value = folder; ClearResults(); }

    internal async Task RunAsync()
    {
        if (busy || shell.IsBusy) return;
        string folder = source.Value, label = name.Text;
        var published = new DateTimeOffset(publishDate.Value).ToUniversalTime();
        bool includeSubfolders = recursive.Checked;
        busy = true; RefreshEnabled(); ClearResults();
        try
        {
            CompilationSnapshot? saved = null;
            CompilationRecencyResult? check = null;
            var outcome = await shell.RunJobAsync(saving ? "Save compilation state" : "Check clips recency",
                $"Folder: {folder}\nInclude subfolders: {includeSubfolders}\nHistory: {shell.CompilationHistory.LogPath}", !saving,
                (progress, ct) =>
                {
                    if (saving)
                    {
                        saved = shell.CompilationHistory.Save(folder, label, includeSubfolders, progress, ct, publishedAtUtc: published);
                        return JobOutcome.Complete($"Saved: {saved.Clips.Count:N0} clips ({saved.Clips.Select(c => c.Hash).Distinct().Count():N0} unique) in {saved.Name}.\n"
                            + $"Publish date: {saved.PublishTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}\nHistory: {shell.CompilationHistory.LogPath}"
                            + (saved.PublishTimeUtc < saved.SavedAtUtc.AddDays(-CompilationHistory.RecencyDays)
                                ? "\nThis publish date is over 30 days old. The record expired immediately and was not retained." : ""));
                    }
                    check = shell.CompilationHistory.Check(folder, includeSubfolders, progress, ct);
                    string summary = $"Recent or scheduled: {check.RecentCount:N0} of {check.Clips.Count:N0} checked clips. Select matches to move or delete."
                        + $"\nSaved compilations: {check.SavedCompilations:N0}. Read errors: {check.Errors.Count:N0}."
                        + (check.SavedCompilations == 0 ? "\nNo compilation history yet. Save finished compilations first." : "")
                        + (check.Errors.Count > 0 ? "\nPARTIAL CHECK: unreadable clips or folders were not checked.\n" + string.Join("\n", check.Errors.Take(8)) : "");
                    return new(check.Errors.Count > 0 ? JobState.Failed : JobState.Completed, summary);
                });
            await UiThread.Switch(this);
            if (IsDisposed) return;
            status.Text = outcome?.Details ?? "Another operation is running. Try again when it finishes.";
            if (saved is not null)
            {
                ShowSavedClips(saved);
                await ReloadHistoryAsync();
                await UiThread.Switch(this);
                if (IsDisposed) return;
                ShowPage(true);
            }
            if (check is not null) { checkedResult = check; ShowResults(); }
            if (outcome?.State == JobState.Completed) shell.RememberPath(folder);
        }
        finally { await UiThread.Switch(this); if (!IsDisposed) { busy = false; RefreshEnabled(); } }
    }

    private void ShowResults()
    {
        if (checkedResult is not { } check) return;
        results.SetRows(check.Clips.Where(c => !recentOnly.Checked || c.RecentCompilations > 0).Select(c =>
        {
            var file = c.Clip.File;
            string state = c.RecentCompilations > 0 ? c.LastUse!.PublishTimeUtc > check.CheckedAtUtc ? "SCHEDULED" : "RECENT" : "No saved use";
            return new ContextRow(file.Path, state, state, file.Path, c.LastUse?.Name ?? "No saved use", Path.GetDirectoryName(file.Path)!,
                "", WorkflowWorkspace.FormatSize(file.Length), Selectable: c.RecentCompilations > 0, Bytes: file.Length, Modified: c.LastUse?.PublishTimeUtc,
                Identity: "SHA-256: " + c.Clip.Hash + $"\nRecent compilations: {c.RecentCompilations:N0}"
                    + (c.LastUse is { } last ? $"\nCompilation: {last.Name}\nCompilation folder at save: {last.Folder}\nPublish date: {last.PublishTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm zzz}" : "\nNo use recorded in retained history."));
        }).ToArray());
        results.SetContextSummary($"{check.RecentCount:N0} recent or scheduled / {check.Clips.Count:N0} checked. Publications from {check.CheckedAtUtc.AddDays(-CompilationHistory.RecencyDays).ToLocalTime():yyyy-MM-dd HH:mm}, including future dates."
            + (check.Errors.Count > 0 ? " PARTIAL CHECK — see read errors below." : "")
            + (recentOnly.Checked ? " Showing recent clips only." : " Showing all checked clips."));
        ShowPage(true);
        RefreshEnabled();
    }

    private void RefreshEnabled() => SetBusy(shell.IsBusy);
    internal void SetBusy(bool shellBusy)
    {
        bool enabled = !busy && !shellBusy;
        source.Enabled = recursive.Enabled = name.Enabled = publishDate.Enabled = run.Enabled = enabled;
        if (clipActions is not null) clipActions.Enabled = enabled;
        if (moveSelected is not null) moveSelected.Enabled = enabled && checkedResult is not null && results.SelectedKeys.Count > 0;
        if (deleteSelected is not null) deleteSelected.Enabled = enabled && checkedResult is not null && results.SelectedKeys.Count > 0;
        RefreshHistoryEnabled(enabled);
    }
}
