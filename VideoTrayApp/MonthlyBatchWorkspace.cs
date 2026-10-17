namespace VideoTrayApp;

internal sealed class MonthlyBatchWorkspace : UserControl
{
    private readonly MainShell shell;
    private readonly PathPicker source, output;
    private readonly DateTimePicker month = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "MMMM yyyy",
        ShowUpDown = true, Width = 210, AccessibleName = "Best of the month (completed months only)" };
    private readonly FileContextList files = new(contextOnly: true) { Dock = DockStyle.Fill };
    private readonly DetailsBox status = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Bottom, Height = 100, AccessibleName = "Monthly batch status" };
    private readonly DesktopButton analyze, prepare;
    private readonly Panel configuration = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Panel review = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly DesktopButton configureTab, reviewTab;
    private MonthlyBatchPlan? plan;
    private bool working;
    private int revision;

    internal MonthlyBatchWorkspace(MainShell shell, UiPreferences preferences, MonthlyBatchSettings settings, string workingFolder)
    {
        this.shell = shell;
        Font = DesktopTheme.BodyFont; AutoScroll = true;
        files.Table.Columns.Add(new DataGridViewTextBoxColumn { Name = "Created", HeaderText = "Created (local)", Width = 175,
            SortMode = DataGridViewColumnSortMode.Programmatic, DisplayIndex = 3 });
        files.Table.Columns["Group"]!.Visible = false;
        files.Table.Columns["Modified"]!.Visible = false;
        source = new("Source / archive directory (includes subfolders)", recent: preferences.RecentPaths)
        { Value = settings.ArchiveDirectory.Length > 0 ? settings.ArchiveDirectory : workingFolder };
        output = new("Output directory", recent: preferences.RecentPaths) { Value = settings.OutputDirectory };
        RefreshMonthLimit(); month.Value = month.MaxDate;
        var fields = DesktopTheme.Stack();
        DesktopTheme.Add(fields, DesktopTheme.Text("Best of the month copies every unique MP4 recorded in a completed month into a new batch. "
            + "Filtering uses the creation date inside each MP4 in your local time zone. Clips without that date are excluded. "
            + "Exact duplicates appear once; clips are numbered from shortest to longest."));
        DesktopTheme.Add(fields, source); DesktopTheme.Add(fields, output);
        DesktopTheme.Add(fields, DesktopTheme.Actions(DesktopTheme.Text("Month"), month));
        analyze = DesktopTheme.Button("Analyze / refresh preview", async () => await AnalyzeAsync()); analyze.Tag = CommandRole.Primary;
        DesktopTheme.Add(fields, DesktopTheme.Actions(analyze,
            DesktopTheme.Button("Automatic preparation settings", () => shell.OpenSettingsSection("Best of the month"))));
        configuration.Controls.Add(fields);
        prepare = DesktopTheme.Button("Review and prepare batch", async () => await PrepareAsync()); prepare.Tag = CommandRole.Primary;
        var actions = DesktopTheme.Actions(prepare);
        review.Controls.Add(files); review.Controls.Add(actions);
        review.SizeChanged += (_, _) =>
        {
            files.Dock = review.ClientSize.Height - actions.Height < 320 ? DockStyle.Top : DockStyle.Fill;
            if (files.Dock == DockStyle.Top) files.Height = 390;
        };
        configureTab = DesktopTheme.Button("Configuration", () => ShowReview(false));
        reviewTab = DesktopTheme.Button("Review files", () => ShowReview(true));
        configureTab.Kind = reviewTab.Kind = ButtonKind.Tab;
        var tabs = DesktopTheme.Actions(configureTab, reviewTab);
        var stages = new Panel { Dock = DockStyle.Fill }; stages.Controls.Add(configuration); stages.Controls.Add(review);
        Controls.Add(stages); Controls.Add(status); Controls.Add(tabs);
        source.ValueChanged += (_, _) => Changed(); output.ValueChanged += (_, _) => Changed(); month.ValueChanged += (_, _) => Changed();
        SizeChanged += (_, _) =>
        {
            int minimum = ClientSize.Height * 96 / Math.Max(DeviceDpi, 96) < 430 ? 540 * DeviceDpi / 96 : 0;
            if (AutoScrollMinSize.Height != minimum) AutoScrollMinSize = new Size(0, minimum);
        };
        status.Text = "Choose a source, output directory, and completed month, then analyze.";
        ShowReview(false); SetBusy(false);
    }

    private void RefreshMonthLimit()
    {
        DateTime lastDay = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddDays(-1);
        if (month.MaxDate != lastDay) month.MaxDate = lastDay;
    }
    private void Changed()
    {
        revision++; plan = null; files.SetRows([]);
        prepare.Enabled = false; status.Text = "Configuration changed. Analyze again to prepare a monthly batch.";
    }
    private void ShowReview(bool show)
    {
        configuration.Visible = !show; review.Visible = show;
        if (show) review.BringToFront(); else configuration.BringToFront();
        configureTab.Selected = !show; reviewTab.Selected = show;
    }
    internal void SetBusy(bool busy)
    {
        if (IsDisposed) return;
        RefreshMonthLimit();
        analyze.Enabled = !busy && !working;
        prepare.Enabled = !busy && !working && plan is { Clips.Count: > 0 };
    }
    internal async Task AnalyzeAsync()
    {
        if (working || shell.IsBusy) return;
        RefreshMonthLimit();
        string folder = source.Value, target = output.Value;
        DateOnly selectedMonth = DateOnly.FromDateTime(month.Value);
        int analyzedRevision = revision;
        MonthlyBatchPlan? analyzed = null;
        plan = null; working = true; SetBusy(true);
        try
        {
            var outcome = await shell.RunJobAsync("Best of the month — analyze", $"Source: {folder}\nOutput: {target}\nMonth: {selectedMonth:yyyy-MM}", true,
                (progress, ct) =>
                {
                    analyzed = MonthlyBatchPreparer.Analyze(folder, target, selectedMonth, progress, ct);
                    return new(JobState.ReadyToReview, analyzed.Summary);
                });
            await UiThread.Switch(this);
            if (IsDisposed || outcome is null) return;
            if (outcome.State == JobState.ReadyToReview && analyzed is not null && revision == analyzedRevision)
            {
                plan = analyzed; files.SetRows(plan.ReviewRows());
                shell.RememberPath(folder); shell.RememberPath(target); ShowReview(true);
            }
            status.Text = outcome.Details + (revision != analyzedRevision ? "\nConfiguration changed. Analyze again." : "");
        }
        finally { if (!IsDisposed) { working = false; SetBusy(shell.IsBusy); } }
    }
    internal async Task PrepareAsync()
    {
        if (working || shell.IsBusy || plan is not { Clips.Count: > 0 } frozen) return;
        working = true; SetBusy(true);
        try
        {
            using var confirmation = new ReviewConfirmation("Best of the month", frozen.Summary, frozen.ReviewRows(),
                "Copies are verified before publishing the new batch. Cancellation or failure removes owned staging files. Original clips stay in the source directory.");
            if (confirmation.ShowDialog(shell) != DialogResult.OK) return;
            // Configuration can change while a native confirmation pumps messages.
            if (!ReferenceEquals(plan, frozen)) { status.Text = "Configuration changed. Analyze again."; return; }
            var outcome = await shell.RunJobAsync("Best of the month", frozen.Summary, false,
                (progress, ct) => JobOutcome.Complete(frozen.Execute(progress, ct).Summary));
            await UiThread.Switch(this);
            if (IsDisposed || outcome is null) return;
            status.Text = outcome.Details; plan = null;
        }
        finally { if (!IsDisposed) { working = false; SetBusy(shell.IsBusy); } }
    }
}
