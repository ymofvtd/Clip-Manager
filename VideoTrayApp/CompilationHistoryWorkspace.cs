namespace VideoTrayApp;

internal sealed class CompilationHistoryWorkspace : UserControl
{
    private readonly MainShell shell;
    private readonly bool saving;
    private readonly PathPicker source;
    private readonly TextBox name = new() { Width = 320, PlaceholderText = "Use the folder name", AccessibleName = "Compilation history name" };
    private readonly CheckBox recursive = new() { Text = "Include subfolders", AutoSize = true };
    private readonly CheckBox recentOnly = new() { Text = "Show only recent clips", AutoSize = true, Checked = true };
    private readonly FileContextList results = new(contextOnly: true) { Dock = DockStyle.Fill };
    private readonly DetailsBox status = new() { Dock = DockStyle.Bottom, Height = 95, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, AccessibleName = "Compilation history status" };
    private readonly DesktopButton run;
    private readonly Panel configuration = new() { Dock = DockStyle.Fill };
    private readonly Panel resultPage = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly DesktopButton settingsTab, resultsTab;
    private CompilationRecencyResult? checkedResult;
    private bool busy;

    internal CompilationHistoryWorkspace(MainShell shell, bool saving, string folder, UiPreferences preferences)
    {
        this.shell = shell; this.saving = saving;
        Dock = DockStyle.Fill; Font = DesktopTheme.BodyFont;
        source = new(saving ? "Finished compilation folder" : "Folder to check", recent: preferences.RecentPaths) { Value = folder };
        var fields = DesktopTheme.Stack(); fields.Padding = new Padding(24, 12, 24, 12);
        DesktopTheme.Add(fields, DesktopTheme.Text(saving
            ? "Save the individual MP4 clips used in a finished compilation. Each save records their content hashes and today's date in your shared history. Save when the compilation is finished or published."
            : "Flag MP4 clips used in any compilation saved during the last 30 days, even after copying or renaming. Only saved compilations can be checked. Edited or re-encoded files have different hashes."));
        DesktopTheme.Add(fields, source);
        if (saving) DesktopTheme.Add(fields, DesktopTheme.Actions(DesktopTheme.Text("Compilation name"), name));
        run = DesktopTheme.Button(saving ? "Save compilation state" : "Check clips recency", async () => await RunAsync());
        run.Tag = CommandRole.Primary;
        var actions = DesktopTheme.Actions(recursive, run);
        actions.Dock = DockStyle.Bottom; actions.Padding = new Padding(20, 6, 16, 6);
        var scrollFields = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; scrollFields.Controls.Add(fields);
        configuration.Controls.Add(scrollFields); configuration.Controls.Add(actions);
        results.Table.AccessibleName = saving ? "Saved compilation clips" : "Clip recency results";
        results.ModifiedTimeDescription = "Last saved";
        results.SetContextSummary(saving ? "No compilation saved here yet." : "No recency check yet.");
        results.Table.Columns[0].Visible = results.Table.Columns[5].Visible = false;
        results.Table.Columns[1].HeaderText = saving ? "State" : "Recency"; results.Table.Columns[1].Width = 110;
        results.Table.Columns[2].Width = 150;
        results.Table.Columns[4].HeaderText = saving ? "Compilation" : "Last compilation"; results.Table.Columns[4].Width = 200;
        results.Table.Columns[7].HeaderText = "Last saved (local)"; results.Table.Columns[7].Width = 150;
        results.Table.Columns[6].Width = 85;
        int displayIndex = 0;
        foreach (int column in new[] { 1, 2, 4, 7, 6, 3 }) results.Table.Columns[column].DisplayIndex = displayIndex++;
        resultPage.Controls.Add(results);
        var pages = new Panel { Dock = DockStyle.Fill }; pages.Controls.Add(configuration); pages.Controls.Add(resultPage);
        settingsTab = DesktopTheme.Button("Settings", () => ShowPage(false));
        resultsTab = DesktopTheme.Button(saving ? "Saved clips" : "Recency results", () => ShowPage(true));
        settingsTab.Kind = resultsTab.Kind = ButtonKind.Tab;
        var tabs = DesktopTheme.Actions(settingsTab, resultsTab); tabs.Padding = new Padding(20, 8, 16, 0);
        if (!saving) tabs.Controls.Add(recentOnly);
        Controls.Add(pages); Controls.Add(status); Controls.Add(tabs);
        ShowPage(false);
        source.ValueChanged += (_, _) => ClearResults();
        recursive.CheckedChanged += (_, _) => ClearResults();
        recentOnly.CheckedChanged += (_, _) => ShowResults();
        status.Text = saving
            ? "Choose the folder containing the individual clips, rather than the exported compilation video. Saving leaves your videos in place."
            : "Choose a folder to check. No saved use means the clip is absent from this history; older compilations must be saved first.";
        VisibleChanged += (_, _) => { if (Visible) RefreshEnabled(); };
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
                        saved = shell.CompilationHistory.Save(folder, label, includeSubfolders, progress, ct);
                        return JobOutcome.Complete($"Saved: {saved.Clips.Count:N0} clips ({saved.Clips.Select(c => c.Hash).Distinct().Count():N0} unique) in {saved.Name}.\n"
                            + $"Saved at: {saved.SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\nHistory: {shell.CompilationHistory.LogPath}");
                    }
                    check = shell.CompilationHistory.Check(folder, includeSubfolders, progress, ct);
                    string summary = $"Flagged as recent (last {CompilationHistory.RecencyDays} days): {check.RecentCount:N0} of {check.Clips.Count:N0} checked clips."
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
                results.SetRows(saved.Clips.Select(c => new ContextRow(c.Name, "", "Saved", Path.Combine(saved.Folder, c.Name), saved.Name,
                    Path.GetDirectoryName(Path.Combine(saved.Folder, c.Name))!, "", WorkflowWorkspace.FormatSize(c.Size), Bytes: c.Size,
                    Modified: saved.SavedAtUtc, Identity: "SHA-256: " + c.Hash)).ToArray());
                results.SetContextSummary($"Saved compilation: {saved.Name}. {saved.Clips.Count:N0} clips recorded.");
                ShowPage(true);
            }
            if (check is not null) { checkedResult = check; ShowResults(); }
            if (outcome?.State == JobState.Completed) shell.RememberPath(folder);
        }
        finally { if (!IsDisposed) { busy = false; RefreshEnabled(); } }
    }

    private void ShowResults()
    {
        if (checkedResult is not { } check) return;
        results.SetRows(check.Clips.Where(c => !recentOnly.Checked || c.RecentCompilations > 0).Select(c =>
        {
            var file = c.Clip.File;
            string state = c.RecentCompilations > 0 ? "RECENT" : c.LastUse is null ? "No saved use" : "Over 30 days";
            return new ContextRow(file.Path, state, state, file.Path, c.LastUse?.Name ?? "No saved use", Path.GetDirectoryName(file.Path)!,
                "", WorkflowWorkspace.FormatSize(file.Length), Bytes: file.Length, Modified: c.LastUse?.SavedAtUtc,
                Identity: "SHA-256: " + c.Clip.Hash + $"\nRecent compilations: {c.RecentCompilations:N0}"
                    + (c.LastUse is { } last ? $"\nLast compilation: {last.Name}\nCompilation folder at save: {last.Folder}\nLast saved: {last.SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}" : "\nNo use recorded in saved history."));
        }).ToArray());
        results.SetContextSummary($"{check.RecentCount:N0} recent clips / {check.Clips.Count:N0} checked. Window: {check.CheckedAtUtc.AddDays(-CompilationHistory.RecencyDays).ToLocalTime():yyyy-MM-dd HH:mm} through {check.CheckedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}."
            + (check.Errors.Count > 0 ? " PARTIAL CHECK — see read errors below." : "")
            + (recentOnly.Checked ? " Showing recent clips only." : " Showing all checked clips."));
        ShowPage(true);
    }

    private void RefreshEnabled() => SetBusy(shell.IsBusy);
    internal void SetBusy(bool shellBusy)
    { source.Enabled = recursive.Enabled = name.Enabled = run.Enabled = !busy && !shellBusy; }
}
