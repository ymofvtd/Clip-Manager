using System.Globalization;

namespace VideoTrayApp;

internal sealed class ClipSeekWorkspace : UserControl
{
    private readonly MainShell shell;
    private readonly PathPicker source;
    private readonly DateTimePicker from = new() { Format = DateTimePickerFormat.Short, Width = 145, Value = DateTime.Today, AccessibleName = "Created from date" };
    private readonly DateTimePicker through = new() { Format = DateTimePickerFormat.Short, Width = 145, Value = DateTime.Today, AccessibleName = "Created through date" };
    private readonly CheckBox recursive = new() { Text = "Include subfolders", AutoSize = true, Checked = true };
    private readonly CheckBox useDates = new() { Text = "Filter by creation date", AutoSize = true, Checked = true, AccessibleName = "Filter by creation date" };
    private readonly CheckBox useDuration = new() { Text = "Filter by duration", AutoSize = true, AccessibleName = "Filter by duration" };
    private readonly TextBox timecode = new() { Text = "00:30:00", Width = 135, AccessibleName = "Duration MM:SS:FF" };
    private readonly ComboBox clipFps = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 185, AccessibleName = "Clip frame rate filter" };
    private readonly ComboBox timecodeFps = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 205, AccessibleName = "Timecode frame rate" };
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270, AccessibleName = "Duration matching option" };
    private readonly ComboBox output = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, AccessibleName = "Seek clips result mode" };
    private readonly CheckBox includeUnknown = new() { Text = "Also show clips with no embedded creation date", AutoSize = true };
    private readonly FileContextList results = new(lookup: true) { Dock = DockStyle.Fill };
    private readonly DetailsBox status = new() { Dock = DockStyle.Bottom, Height = 80, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, AccessibleName = "Seek clips status", Text = "Choose a folder and optional date or duration filters, then Find clips." };
    private readonly Button find;
    private readonly DesktopButton move, delete, another, detectRates;
    private IReadOnlyList<DatedClip> matches = [];
    private bool actionBusy, searchBusy;
    private string? previousRandom;
    private string searchContext = "";
    private const string Recovery = "Selected files and exact move destinations are verified again before running. Cancellation or failure attempts in-process rollback; forced termination is outside that guarantee. Successful deletion uses Windows Recycle Bin.";
    private readonly Panel configuration = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Panel resultPage = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly DesktopButton configureTab, resultsTab;
    private int revision;
    private bool updatingRates;
    private const string AllRates = "All frame rates", AutoRate = "Automatic (clip FPS)";

    internal ClipSeekWorkspace(MainShell shell, Func<string> workingFolder, UiPreferences preferences)
    {
        this.shell = shell;
        results.AttachCompilation(shell);
        Font = DesktopTheme.BodyFont; Dock = DockStyle.Fill;
        source = new("Search folder", recent: preferences.RecentPaths) { Value = workingFolder() };
        var fields = DesktopTheme.Stack(); fields.Padding = new Padding(24, 12, 24, 12);
        DesktopTheme.Add(fields, DesktopTheme.Text("Seek clips by creation date, duration, or both. Dates use the MP4's embedded creation date in your local time zone. Include all clip frame rates or choose a detected/custom rate. Choose all matches or one random clip."));
        DesktopTheme.Add(fields, source);
        DesktopTheme.Add(fields, DesktopTheme.Actions(useDates, DesktopTheme.Text("From"), from, DesktopTheme.Text("Through (inclusive)"), through));
        DesktopTheme.Add(fields, includeUnknown);
        clipFps.Text = AllRates; timecodeFps.Text = AutoRate; SetFrameRates([]);
        detectRates = DesktopTheme.Button("Detect frame rates", async () => await DetectFrameRatesAsync());
        DesktopTheme.Add(fields, DesktopTheme.Actions(DesktopTheme.Text("Clip FPS"), clipFps, detectRates));
        mode.Items.AddRange(["Exact duration (nearest frame)", "Within 5 seconds shorter or longer", "Up to 5 seconds shorter", "Up to 5 seconds longer"]); mode.SelectedIndex = 0;
        output.Items.AddRange(["Show all matching clips", "Pick one random clip"]); output.SelectedIndex = 0;
        DesktopTheme.Add(fields, DesktopTheme.Actions(useDuration, DesktopTheme.Text("MM:SS:FF"), timecode, mode));
        DesktopTheme.Add(fields, DesktopTheme.Actions(DesktopTheme.Text("Interpret FF using"), timecodeFps));
        DesktopTheme.Add(fields, DesktopTheme.Text("Automatic interprets FF at each clip's actual FPS; Clip FPS only limits which clips qualify. Enter a custom timecode FPS if needed. Rates are shown as averages for variable-frame-rate recordings. Clips with unknown FPS can match Automatic when FF is 00."));
        find = DesktopTheme.Button("Find clips", async () => await FindAsync()); find.Tag = CommandRole.Primary;
        var commands = DesktopTheme.Actions(recursive, output, find); commands.Dock = DockStyle.Bottom; commands.Padding = new Padding(20, 6, 16, 6);
        var scrollFields = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; scrollFields.Controls.Add(fields);
        configuration.AutoScroll = false; configuration.Controls.Add(scrollFields); configuration.Controls.Add(commands);
        move = DesktopTheme.Button("Move selected…", async () => await ChooseMoveAsync());
        delete = DesktopTheme.Button("Delete selected…", async () => await ActAsync(null)); delete.Tag = CommandRole.Recycle;
        another = DesktopTheme.Button("Find another random clip", async () => await FindAsync()); another.Visible = false;
        var actions = DesktopTheme.Actions(DesktopTheme.Button("Select all visible", results.SelectVisible),
            DesktopTheme.Button("Clear selection", results.ClearSelection), move, delete, another);
        actions.Padding = new Padding(20, 6, 16, 6);
        actions.AccessibleName = "Actions for selected sought clips";
        results.Table.AccessibleName = "Seek clips results";
        results.Table.Columns[2].Width = 160; results.Table.Columns[3].Width = 170;
        results.Table.Columns[4].Visible = false; results.Table.Columns[6].Width = 95;
        results.Table.Columns["Duration"]!.Width = 145; results.Table.Columns["Duration"]!.DisplayIndex = 3;
        results.Table.Columns.Add(new DataGridViewTextBoxColumn { Name = "FrameRate", HeaderText = "FPS (average)", Width = 120,
            SortMode = DataGridViewColumnSortMode.Programmatic, DisplayIndex = 4 });
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
        source.ValueChanged += (_, _) => { SetFrameRates([]); Changed(); };
        from.ValueChanged += (_, _) => Changed(); through.ValueChanged += (_, _) => Changed();
        recursive.CheckedChanged += (_, _) => { SetFrameRates([]); Changed(); }; includeUnknown.CheckedChanged += (_, _) => Changed();
        useDates.CheckedChanged += (_, _) => { RefreshFilters(); Changed(); };
        useDuration.CheckedChanged += (_, _) => { RefreshFilters(); Changed(); };
        timecode.TextChanged += (_, _) => Changed();
        clipFps.TextChanged += (_, _) => { if (!updatingRates) Changed(); };
        timecodeFps.TextChanged += (_, _) => { if (!updatingRates) Changed(); };
        mode.SelectedIndexChanged += (_, _) => Changed(); output.SelectedIndexChanged += (_, _) => Changed();
        results.SelectionChanged += RefreshActions;
        RefreshFilters(); RefreshActions();
    }

    private void RefreshFilters()
    {
        from.Enabled = through.Enabled = includeUnknown.Enabled = useDates.Checked;
        timecode.Enabled = timecodeFps.Enabled = mode.Enabled = useDuration.Checked;
    }

    private void SetFrameRates(IReadOnlyList<decimal> rates)
    {
        updatingRates = true;
        try
        {
            string selectedClip = clipFps.Text, selectedTimecode = timecodeFps.Text;
            clipFps.Items.Clear(); clipFps.Items.Add(AllRates);
            clipFps.Items.AddRange(rates.Select(FormatRate).Cast<object>().ToArray());
            timecodeFps.Items.Clear(); timecodeFps.Items.Add(AutoRate);
            timecodeFps.Items.AddRange(rates.Concat([23.976m, 24m, 25m, 29.97m, 30m, 50m, 59.94m, 60m, 120m])
                .Distinct().Order().Select(FormatRate).Cast<object>().ToArray());
            clipFps.Text = selectedClip; timecodeFps.Text = selectedTimecode;
        }
        finally { updatingRates = false; }
    }

    private static string FormatRate(decimal rate) => rate.ToString("0.###", CultureInfo.InvariantCulture);
    private static decimal? ParseRate(string text, string automatic)
    {
        if (string.Equals(text.Trim(), automatic, StringComparison.OrdinalIgnoreCase)) return null;
        if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal rate) || rate is < 0.001m or > 1000)
            throw new ArgumentException($"Choose {automatic} or enter a frame rate from 0.001 to 1000, such as 58 or 59.97.");
        return rate;
    }

    internal async Task DetectFrameRatesAsync()
    {
        if (shell.IsBusy || actionBusy || searchBusy) return;
        string folder = source.Value; bool recurse = recursive.Checked; int detectedRevision = revision;
        searchBusy = true; RefreshActions();
        try
        {
            FrameRateDiscovery? discovery = null;
            var outcome = await shell.RunJobAsync("Detect clip frame rates", $"Search folder: {folder}\nInclude subfolders: {recurse}", true, (progress, ct) =>
            {
                discovery = Mp4FrameRate.Discover(folder, recurse, progress, ct);
                return new(discovery.Errors.Count > 0 ? JobState.CompletedWithIssues : JobState.Completed,
                    $"Detected {discovery.Rates.Count:N0} frame rates in {discovery.Scanned:N0} clips: " + string.Join(", ", discovery.Rates.Select(FormatRate))
                    + $". Unknown FPS: {discovery.Unknown:N0}. Read/scan errors: {discovery.Errors.Count:N0}."
                    + (discovery.Errors.Count > 0 ? "\n" + string.Join("\n", discovery.Errors.Take(12)) : ""));
            });
            await UiThread.Switch(this);
            if (IsDisposed || outcome is null) return;
            status.Text = outcome.Details;
            if (revision == detectedRevision && discovery is not null) SetFrameRates(discovery.Rates);
            else status.Text += "\nSettings changed during detection. Detect again to refresh the available rates.";
        }
        finally { if (!IsDisposed) { searchBusy = false; RefreshActions(); } }
    }

    private void Changed()
    {
        revision++; previousRandom = null; matches = []; results.SetRows([]); another.Visible = false;
        status.Text = "Settings changed. Find clips again to refresh the results."; RefreshActions();
    }

    private void ShowResults(bool show)
    {
        configuration.Visible = !show; resultPage.Visible = show;
        if (show) resultPage.BringToFront(); else configuration.BringToFront();
        configureTab.Selected = !show; resultsTab.Selected = show;
    }

    private void RefreshActions()
    {
        move.Enabled = delete.Enabled = !actionBusy && !searchBusy && results.SelectedKeys.Count > 0;
        find.Enabled = another.Enabled = !actionBusy && !searchBusy;
        detectRates.Enabled = !actionBusy && !searchBusy;
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
        string title = destination is null ? "Delete selected clips" : "Move selected clips";
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
        if (shell.IsBusy || actionBusy || searchBusy) return;
        ClipSeekOptions options;
        try
        {
            decimal? interpretation = useDuration.Checked ? ParseRate(timecodeFps.Text, AutoRate) : null;
            DurationQuery? duration = !useDuration.Checked ? null : interpretation is { } rate
                ? DurationQuery.Parse(timecode.Text, rate, (DurationMatchMode)mode.SelectedIndex)
                : DurationQuery.ParseAutomatic(timecode.Text, (DurationMatchMode)mode.SelectedIndex);
            options = new(useDates.Checked ? DateOnly.FromDateTime(from.Value) : null,
                useDates.Checked ? DateOnly.FromDateTime(through.Value) : null,
                duration, useDates.Checked && includeUnknown.Checked, output.SelectedIndex == 1, ParseRate(clipFps.Text, AllRates));
            options.Validate();
        }
        catch (ArgumentException ex) { status.Text = ex.Message; return; }
        string folder = source.Value;
        bool recurse = recursive.Checked;
        string durationContext = options.Duration is null ? "Any duration" : $"{timecode.Text}; timecode FPS: {timecodeFps.Text}; {mode.Text}";
        int searchedRevision = revision;
        string? previous = previousRandom;
        ClipSeekResult? scan = null;
        matches = []; results.SetRows([]); searchBusy = true; RefreshActions();
        try
        {
            string dateContext = options.From is { } first && options.Through is { } last
                ? $"{first:yyyy-MM-dd} through {last:yyyy-MM-dd} (inclusive, {TimeZoneInfo.Local.DisplayName}); include unknown dates: {options.IncludeUnknownDates}"
                : "Any creation date";
            searchContext = $"Search folder: {folder}\nCreated: {dateContext}\nDuration: {durationContext}\nClip FPS: {clipFps.Text}\nInclude subfolders: {recurse}; result mode: {(options.OneRandom ? "One random clip" : "All matches")}";
            var outcome = await shell.RunJobAsync("Seek clips", searchContext, true,
                (progress, ct) =>
                {
                    scan = ClipSeekLookup.Find(folder, options, recurse, TimeZoneInfo.Local, progress, ct, previous);
                    string summary = $"{scan.Eligible:N0} matching clips from {scan.Scanned:N0} MP4 clips. "
                        + (options.OneRandom ? $"Showing {scan.Matches.Count:N0} random result. " : $"Showing all {scan.Matches.Count:N0} results. ")
                        + $"Missing/unsupported creation dates: {scan.Unknown:N0}. Unknown clip FPS: {scan.UnknownFrameRates:N0}. Read/scan errors: {scan.Errors.Count:N0}."
                        + (options.Duration is { UseClipFrameRate: true, FrameField: > 0 } && scan.UnknownFrameRates > 0 ? "\nClips with unknown FPS cannot match nonzero FF in Automatic mode; choose an explicit timecode FPS to include them." : "")
                        + (scan.Errors.Count > 0 ? "\nIncomplete scan:\n" + string.Join("\n", scan.Errors.Take(20)) : "");
                    return new(scan.Errors.Count > 0 ? JobState.CompletedWithIssues : JobState.Completed, summary);
                });
            await UiThread.Switch(this);
            if (IsDisposed || outcome is null) return;
            status.Text = outcome.Details;
            if (searchedRevision != revision) { status.Text += "\nSettings changed during search. Find clips again to refresh."; return; }
            if (scan is not null)
            {
                matches = scan.Matches.Select(c => c.File).ToArray();
                SetFrameRates(scan.FrameRates);
                results.Table.Columns["Duration"]!.Visible = options.Duration is not null;
                results.SetRows(scan.Matches.Select(c => new ContextRow(c.File.Path, "", "", c.File.Path,
                    c.File.Created is null ? "No embedded date" : "Embedded MP4 movie creation date", Path.GetDirectoryName(c.File.Path)!,
                    c.Duration is { } duration ? options.Duration!.Format(duration, c.FrameRate) + $" ({duration.TotalSeconds:0.###}s)" : "",
                    WorkflowWorkspace.FormatSize(c.File.Size), Selectable: true, Bytes: c.File.Size, Created: c.File.Created,
                    Modified: c.File.LastWriteUtc, Identity: c.File.Physical + " / " + c.File.Stamp, FrameRate: c.FrameRate)).ToArray());
                previousRandom = options.OneRandom ? scan.Matches.FirstOrDefault()?.File.Path : null;
                another.Visible = options.OneRandom;
                shell.RememberPath(folder);
            }
            ShowResults(true);
        }
        finally { if (!IsDisposed) { searchBusy = false; RefreshActions(); } }
    }
}
