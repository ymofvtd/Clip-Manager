namespace VideoTrayApp;

internal sealed partial class CompilationHistoryWorkspace
{
    private async Task ChooseMoveAsync()
    {
        if (shell.IsBusy || busy || results.SelectedKeys.Count == 0) return;
        using var picker = new FolderBrowserDialog { Description = "Choose where to move selected recent clips", UseDescriptionForTitle = true };
        if (picker.ShowDialog(shell) == DialogResult.OK) await ActAsync(picker.SelectedPath);
    }

    internal async Task ActAsync(string? destination)
    {
        if (saving || shell.IsBusy || busy || checkedResult is not { } check) return;
        var selected = results.SelectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0) { status.Text = "Select recent or scheduled clips first, or use Select all visible."; return; }
        string title = destination is null ? "Delete selected recent clips" : "Move selected recent clips";
        string context = $"Checked folder: {source.Value}\nSelected: {selected.Count:N0} clips (including rows hidden by filters)."
            + (destination is null ? "\nAction: Send to Windows Recycle Bin" : "\nMove destination: " + destination);
        if (shell.Archives.Routes.Any(r => r.Worker.Running && selected.Any(p => ArchivePaths.Contains(r.Worker.Settings.Destination, p))))
            context += "\nArchive is watching: removed copies may be archived again from surviving watched originals.";
        busy = true; RefreshEnabled();
        try
        {
            ClipDateActionPlan? plan = null;
            var prepared = await shell.RunJobAsync(title + " — prepare review", context, true, (progress, ct) =>
            {
                plan = CompilationRecencyActions.Prepare(shell.CompilationHistory, check, selected, destination, progress, ct);
                return new(JobState.ReadyToReview, $"{plan.Changes.Count:N0} selected clips ready for review.");
            });
            await UiThread.Switch(this);
            if (IsDisposed || prepared is null) return;
            if (prepared.State != JobState.ReadyToReview || plan is null) { status.Text = prepared.Details; return; }
            using var confirmation = new ReviewConfirmation(title, context, plan.ReviewRows(),
                "Files and destinations are verified again before running. Cancellation or failure attempts rollback. Successful deletion uses Windows Recycle Bin. Compilation history is retained.");
            if (confirmation.ShowDialog(shell) != DialogResult.OK) { status.Text = "Action cancelled before making changes. Selection retained."; return; }
            var outcome = await shell.RunJobAsync(title, context, false, (progress, ct) => JobOutcome.Complete(plan.Execute(progress, ct)));
            await UiThread.Switch(this);
            if (IsDisposed || outcome is null) return;
            if (outcome.State == JobState.Completed)
            {
                checkedResult = check with { Clips = check.Clips.Where(c => !selected.Contains(c.Clip.File.Path)).ToArray() };
                ShowResults();
                if (destination is not null) shell.RememberPath(destination);
            }
            else ClearResults();
            status.Text = outcome.Details;
        }
        finally { if (!IsDisposed) { busy = false; RefreshEnabled(); } }
    }
}
