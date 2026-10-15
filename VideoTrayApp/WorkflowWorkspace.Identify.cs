namespace VideoTrayApp;

internal sealed partial class WorkflowWorkspace
{
    private IdentifyClipScan? identifyScan;
    private DesktopButton? moveSelected, deleteSelected;
    private bool identifyActionBusy;

    private FlowLayoutPanel BuildIdentifyCommands()
    {
        moveSelected = DesktopTheme.Button("Move selected…", async () => await ChooseIdentifyMoveAsync());
        deleteSelected = DesktopTheme.Button("Delete selected…", async () => await ActOnIdentifiedAsync(null));
        deleteSelected.Tag = CommandRole.Recycle;
        var actions = DesktopTheme.Actions(DesktopTheme.Button("Select all visible", review.SelectVisible),
            DesktopTheme.Button("Clear selection", review.ClearSelection), moveSelected, deleteSelected);
        actions.Padding = new Padding(20, 6, 16, 6);
        actions.AccessibleName = "Actions for selected identified clips";
        RefreshIdentifyActions();
        return actions;
    }

    private void RefreshIdentifyActions()
    {
        if (moveSelected is null || deleteSelected is null) return;
        moveSelected.Enabled = deleteSelected.Enabled = !busy && !identifyActionBusy && identifyScan is not null && review.SelectedKeys.Count > 0;
    }

    private async Task ChooseIdentifyMoveAsync()
    {
        if (jobs.Busy || identifyActionBusy || identifyScan is null || review.SelectedKeys.Count == 0) return;
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose the folder to isolate selected clips in", UseDescriptionForTitle = true,
            SelectedPath = destination.Value
        };
        if (picker.ShowDialog(shell) == DialogResult.OK) await ActOnIdentifiedAsync(picker.SelectedPath);
    }

    internal async Task ActOnIdentifiedAsync(string? target)
    {
        if (jobs.Busy || identifyActionBusy || identifyScan is not { } scan) return;
        var selected = new HashSet<string>(review.SelectedKeys, StringComparer.OrdinalIgnoreCase);
        int actionRevision = revision;
        if (selected.Count == 0) { result.Text = "Select clips in the results first, or use Select all visible."; return; }
        string title = target is null ? "Delete selected identified clips" : "Move selected identified clips";
        string context = frozenContext + $"\nSelected: {selected.Count:N0} clips (including selected rows hidden by filters)."
            + (target is null ? "\nAction: Send to Windows Recycle Bin" : "\nMove destination: " + target);
        if (shell.Archive.Running && selected.Any(path => ArchivePaths.Contains(shell.Archive.Settings.Destination, path)))
            context += "\nArchive is watching: removing these archive copies can cause them to be archived again from surviving watched originals.";
        identifyActionBusy = true; analyze.Enabled = false; RefreshIdentifyActions();
        try
        {
            IdentifyClipActionPlan? plan = null;
            var prepared = await shell.RunJobAsync(title + " — prepare review", context, true, (progress, ct) =>
            {
                plan = scan.Prepare(selected, target, progress, ct);
                return new(JobState.ReadyToReview, "Selected matches ready for exact-file confirmation.");
            });
            await UiThread.Switch(this);
            if (IsDisposed || prepared is null) return;
            if (prepared.State != JobState.ReadyToReview || plan is null) { result.Text = prepared.Details; return; }
            using var confirmation = new ReviewConfirmation(title, context, plan.ReviewRows(), recovery);
            if (confirmation.ShowDialog(shell) != DialogResult.OK) { result.Text = "Action cancelled before making changes. Selection retained."; return; }
            var outcome = await shell.RunJobAsync(title, context, false, (progress, ct) => JobOutcome.Complete(plan.Execute(progress, ct)));
            await UiThread.Switch(this);
            if (IsDisposed || outcome is null) return;
            if (outcome.State == JobState.Completed)
            {
                var acted = plan.Selection.Changes.Select(c => c.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                identifyScan = revision == actionRevision ? scan with { Matches = scan.Matches.Where(file => !acted.Contains(file.Path)).ToArray() } : null;
                review.SetRows(review.Rows.Where(row => !acted.Contains(row.Key)).ToArray());
                if (target is not null) shell.RememberPath(target);
            }
            else identifyScan = null;
            result.Text = outcome.Details + (outcome.State == JobState.Completed ? "" : "\nAnalyze again before retrying to refresh file metadata and paths.");
        }
        finally
        {
            if (!IsDisposed) { identifyActionBusy = false; analyze.Enabled = !busy; RefreshIdentifyActions(); }
        }
    }
}
