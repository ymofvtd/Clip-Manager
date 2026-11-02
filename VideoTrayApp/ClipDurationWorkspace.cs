namespace VideoTrayApp;

internal sealed class ClipDurationWorkspace : UserControl
{
    private readonly MainShell shell;
    private readonly PathPicker source;
    private readonly TextBox timecode = new() { Text = "00:30:00", Width = 135, AccessibleName = "Duration MM:SS:FF" };
    private readonly NumericUpDown fps = new() { Minimum = 1, Maximum = 240, DecimalPlaces = 3, Value = 30, Width = 105,
        AccessibleName = "Timecode frames per second" };
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270,
        AccessibleName = "Duration matching option" };
    private readonly CheckBox recursive = new() { Text = "Include subfolders", Checked = true, AutoSize = true };
    private readonly FileContextList result = new(contextOnly: true) { Dock = DockStyle.Fill };
    private readonly DetailsBox status = new() { Dock = DockStyle.Bottom, Height = 90, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, AccessibleName = "Duration search status", Text = "Choose a folder and duration, then find one random clip." };
    private readonly DesktopButton find, another, settingsTab, resultsTab;
    private readonly Panel settingsPage = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Panel resultsPage = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private DurationClip? current;
    private bool busy;
    private int revision;

    internal ClipDurationWorkspace(MainShell shell, Func<string> workingFolder, UiPreferences preferences)
    {
        this.shell = shell; Font = DesktopTheme.BodyFont; Dock = DockStyle.Fill;
        source = new("Search folder", recent: preferences.RecentPaths) { Value = workingFolder() };
        mode.Items.AddRange(["Exact duration (nearest frame)", "Within 5 seconds shorter or longer", "Up to 5 seconds shorter", "Up to 5 seconds longer"]);
        mode.SelectedIndex = 0;
        var fields = DesktopTheme.Stack(); fields.Padding = new Padding(24, 12, 24, 12);
        DesktopTheme.Add(fields, DesktopTheme.Text("Find by duration. Enter minutes:seconds:frames and the frame rate of your timecode. One random matching MP4 is returned. Exact matches use the nearest frame; five-second ranges include their endpoints."));
        DesktopTheme.Add(fields, source);
        DesktopTheme.Add(fields, DesktopTheme.Actions(DesktopTheme.Text("MM:SS:FF"), timecode, DesktopTheme.Text("Frames / second"), fps));
        find = DesktopTheme.Button("Find random clip", async () => await FindAsync()); find.Tag = CommandRole.Primary;
        DesktopTheme.Add(fields, DesktopTheme.Actions(mode, recursive, find));
        settingsPage.Controls.Add(fields);
        result.Table.Columns[0].Visible = false;
        result.Table.Columns[1].Visible = false;
        result.Table.Columns[4].HeaderText = "Match";
        result.Table.Columns[5].Width = 155;
        result.Table.AccessibleName = "Random duration match";
        result.SetContextSummary("One random duration match. Open the clip or add it to your active compilation.");
        result.AttachCompilation(shell);
        another = DesktopTheme.Button("Find another random clip", async () => await FindAsync());
        var actions = DesktopTheme.Actions(another); actions.Padding = new Padding(20, 4, 0, 4);
        resultsPage.Controls.Add(result); resultsPage.Controls.Add(actions);
        resultsPage.SizeChanged += (_, _) =>
        {
            result.Dock = resultsPage.ClientSize.Height - actions.Height < 280 ? DockStyle.Top : DockStyle.Fill;
            if (result.Dock == DockStyle.Top) result.Height = 340;
        };
        settingsTab = DesktopTheme.Button("Search settings", () => ShowResults(false));
        resultsTab = DesktopTheme.Button("Random result", () => ShowResults(true));
        settingsTab.Kind = resultsTab.Kind = ButtonKind.Tab;
        var tabs = DesktopTheme.Actions(settingsTab, resultsTab); tabs.Padding = new Padding(20, 8, 0, 0);
        var stages = new Panel { Dock = DockStyle.Fill }; stages.Controls.Add(settingsPage); stages.Controls.Add(resultsPage);
        Controls.Add(stages); Controls.Add(status); Controls.Add(tabs); ShowResults(false);
        source.ValueChanged += (_, _) => Changed(); timecode.TextChanged += (_, _) => Changed();
        fps.ValueChanged += (_, _) => Changed(); mode.SelectedIndexChanged += (_, _) => Changed();
        recursive.CheckedChanged += (_, _) => Changed();
    }

    private void ShowResults(bool show)
    {
        settingsPage.Visible = !show; resultsPage.Visible = show;
        if (show) resultsPage.BringToFront(); else settingsPage.BringToFront();
        settingsTab.Selected = !show; resultsTab.Selected = show;
    }

    private void Changed()
    {
        revision++; current = null; result.SetRows([]);
        status.Text = "Settings changed. Find a random clip to refresh the result.";
        find.Text = "Find random clip";
    }

    internal async Task FindAsync()
    {
        if (shell.IsBusy || busy) return;
        DurationQuery query;
        try { query = DurationQuery.Parse(timecode.Text, fps.Value, (DurationMatchMode)mode.SelectedIndex); }
        catch (ArgumentException ex) { status.Text = ex.Message; return; }
        string folder = source.Value, entered = timecode.Text, option = mode.Text;
        bool recurse = recursive.Checked;
        int searchedRevision = revision;
        string? previous = current?.Path;
        current = null; result.SetRows([]); busy = true; find.Enabled = another.Enabled = false;
        try
        {
            ClipDurationResult? scan = null;
            var outcome = await shell.RunJobAsync("Find by duration",
                $"Search folder: {folder}\nDuration: {entered}; FPS: {query.FramesPerSecond}; match: {option}\nInclude subfolders: {recurse}", true,
                (progress, ct) =>
                {
                    scan = ClipDurationLookup.Find(folder, query, recurse, progress, ct, previous);
                    return new(scan.Errors.Count > 0 ? JobState.CompletedWithIssues : JobState.Completed,
                        $"{scan.Matches:N0} matching clips from {scan.Scanned:N0} MP4 clips. "
                        + (scan.Clip is null ? "No matching clip found." : "Showing one random match. Find another to choose again.")
                        + (scan.Errors.Count > 0 ? $"\nIncomplete scan: {scan.Errors.Count:N0} errors.\n" + string.Join("\n", scan.Errors.Take(12)) : ""));
                });
            await UiThread.Switch(this);
            if (IsDisposed || outcome is null) return;
            status.Text = outcome.Details;
            if (searchedRevision != revision) { status.Text += "\nSettings changed during search. Find again to refresh."; return; }
            current = scan?.Clip;
            if (current is { } clip)
            {
                result.SetRows([new(clip.Path, "", "Match", clip.Path, option, Path.GetDirectoryName(clip.Path)!,
                    query.Format(clip.Duration) + $" ({clip.Duration.TotalSeconds:0.###}s)", WorkflowWorkspace.FormatSize(clip.Size),
                    Bytes: clip.Size, Modified: new DateTimeOffset(clip.LastWriteUtc))]);
                find.Text = "Find another random clip";
            }
            shell.RememberPath(folder);
            ShowResults(true);
        }
        finally { if (!IsDisposed) { busy = false; find.Enabled = another.Enabled = true; } }
    }
}
