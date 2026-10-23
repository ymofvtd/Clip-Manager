namespace VideoTrayApp;

internal sealed partial class MainShell
{
    private MonthlyBatchSettings monthlySettings = new();
    private MonthlyBatchWorkspace? monthlyPage;
    private Task? monthlyTask;
    private bool monthlyReady, monthlyRunning;
    private DateTimeOffset nextMonthlyCheck;
    private string monthlyStatus = "Automatic monthly preparation is disabled.";
    private Label? monthlySettingsStatus;

    private Control BuildMonthlyBatchSettings()
    {
        var page = DesktopTheme.Stack();
        var enabled = new CheckBox { Text = "Automatically prepare best of the month", Checked = monthlySettings.Enabled, AutoSize = true };
        var notify = new CheckBox { Text = "Notify when a monthly batch finishes (includes playback duration)", Checked = monthlySettings.NotifyOnCompletion, AutoSize = true };
        var source = new PathPicker("Archive directory (includes subfolders)", recent: preferences.RecentPaths) { Value = monthlySettings.ArchiveDirectory };
        var output = new PathPicker("Output directory", recent: preferences.RecentPaths) { Value = monthlySettings.OutputDirectory };
        monthlySettingsStatus = DesktopTheme.Text(monthlyStatus);
        DesktopTheme.Add(page, DesktopTheme.Text("While Clips Manager is running, prepare each completed month automatically. "
            + "Enabling starts with the previous month; missed months are prepared in order when you reopen the app. "
            + "Uses embedded MP4 creation dates in your local time zone, excludes missing dates and exact duplicates, "
            + "and copies clips into best_of_yyyy-MM folders. Completion notifications include the total playback duration."));
        DesktopTheme.Add(page, enabled); DesktopTheme.Add(page, notify); DesktopTheme.Add(page, source); DesktopTheme.Add(page, output);
        DesktopTheme.Add(page, DesktopTheme.Button("Save monthly preparation settings", () =>
        {
            if (jobs.Busy || monthlyRunning) { monthlySettingsStatus.Text = "Wait for the current operation to finish before saving monthly settings."; return; }
            try
            {
                var next = new MonthlyBatchSettings { Enabled = enabled.Checked, NotifyOnCompletion = notify.Checked, ArchiveDirectory = source.Value, OutputDirectory = output.Value,
                    NextMonth = monthlySettings.NextMonth };
                if (next.Enabled || next.ArchiveDirectory.Length > 0 || next.OutputDirectory.Length > 0) next.Validate(requireAvailable: false);
                bool changedFolders = !string.Equals(next.ArchiveDirectory, monthlySettings.ArchiveDirectory, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(next.OutputDirectory, monthlySettings.OutputDirectory, StringComparison.OrdinalIgnoreCase);
                if (changedFolders) next.NextMonth = null;
                if (next.Enabled && next.NextMonth is null)
                    next.NextMonth = MonthlyBatchSettings.MonthStart(DateOnly.FromDateTime(DateTime.Today)).AddMonths(-1);
                next.Save(dataFolder); monthlySettings = next; nextMonthlyCheck = default;
                monthlyStatus = next.Enabled ? $"Enabled. Next month to prepare: {next.NextMonth:MMMM yyyy}. Checking shortly…" : "Automatic monthly preparation is disabled.";
                monthlySettingsStatus.Text = monthlyStatus;
                RememberPath(next.ArchiveDirectory); RememberPath(next.OutputDirectory);
            }
            catch (Exception ex) { monthlySettingsStatus.Text = "Monthly settings could not be saved: " + ex.Message; }
        }));
        DesktopTheme.Add(page, monthlySettingsStatus);
        return page;
    }

    private void CheckMonthlyBatch()
    {
        if (!monthlyReady || monthlyRunning || shuttingDown || jobs.Busy || !monthlySettings.Enabled || DateTimeOffset.UtcNow < nextMonthlyCheck) return;
        nextMonthlyCheck = DateTimeOffset.UtcNow.AddMinutes(1);
        var month = monthlySettings.DueMonth(DateOnly.FromDateTime(DateTime.Today));
        if (month is null) return;
        // Let the app's archiver finish its queued copies before selecting the completed month's inventory.
        if (archives.Routes.Any(route => route.Worker.Running && ClipIdentifier.SamePath(route.Worker.Settings.Destination, monthlySettings.ArchiveDirectory) && !route.Worker.ReadyForMonthlyBatch))
        {
            nextMonthlyCheck = DateTimeOffset.UtcNow.AddSeconds(2);
            monthlyStatus = "Waiting for Archive to finish discovery and pending copies before preparing " + month.Value.ToString("MMMM yyyy") + ".";
            if (monthlySettingsStatus is not null) monthlySettingsStatus.Text = monthlyStatus;
            return;
        }
        monthlyRunning = true;
        monthlyTask = RunAutomaticMonthlyBatchAsync(month.Value);
    }

    private async Task RunAutomaticMonthlyBatchAsync(DateOnly month)
    {
        try
        {
            monthlyStatus = $"Preparing best of {month:MMMM yyyy}…";
            var settings = new MonthlyBatchSettings { Enabled = true, ArchiveDirectory = monthlySettings.ArchiveDirectory,
                OutputDirectory = monthlySettings.OutputDirectory, NotifyOnCompletion = monthlySettings.NotifyOnCompletion, NextMonth = month.AddMonths(1) };
            var outcome = await RunJobAsync("Automatic best of the month",
                $"Month: {month:yyyy-MM}\nArchive: {settings.ArchiveDirectory}\nOutput: {settings.OutputDirectory}", false,
                (progress, ct) =>
                {
                    var result = MonthlyBatchPreparer.PrepareAutomatic(settings, month, progress, ct);
                    // A published batch has a manifest so restart can recover if this save fails.
                    settings.Save(dataFolder);
                    return JobOutcome.Complete(result.Summary);
                });
            await UiThread.Switch(this);
            if (outcome is not null)
            {
                monthlyStatus = outcome.Details;
                if (outcome.State == JobState.Completed) { monthlySettings = settings; nextMonthlyCheck = DateTimeOffset.UtcNow.AddSeconds(1); }
                else nextMonthlyCheck = DateTimeOffset.UtcNow.AddHours(1);
            }
        }
        catch (Exception ex)
        {
            await UiThread.Switch(this);
            monthlyStatus = "Automatic monthly preparation needs attention: " + ex.Message;
            nextMonthlyCheck = DateTimeOffset.UtcNow.AddHours(1);
        }
        finally
        {
            monthlyRunning = false;
            if (!IsDisposed && !shuttingDown && monthlySettingsStatus is not null) monthlySettingsStatus.Text = monthlyStatus;
        }
    }
}
