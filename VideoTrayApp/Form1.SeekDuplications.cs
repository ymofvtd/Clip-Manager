namespace VideoTrayApp;

public partial class Form1
{
    private async Task RunSeekDuplicationsAsync(string? rootA = null, string? rootB = null, bool recursive = true)
    {
        if (operationCts is not null) return;
        if (rootA is null && await TryRunDefaultPresetAsync(PresetAction.SeekDuplications)) return;
        ShowWindow(this, EventArgs.Empty);
        if (rootA is null || rootB is null)
        {
            using var a = new FolderBrowserDialog { Description = "Seek duplications — choose directory A", UseDescriptionForTitle = true };
            if (a.ShowDialog(this) != DialogResult.OK) return;
            using var b = new FolderBrowserDialog { Description = "Seek duplications — choose directory B (subfolders included)", UseDescriptionForTitle = true };
            if (b.ShowDialog(this) != DialogResult.OK) return;
            rootA = a.SelectedPath; rootB = b.SelectedPath;
        }
        SetButtonsEnabled(false);
        await BeginEmbeddedOperationAsync("Seek duplications");
        var progress = new EmbeddedOperationProgress(this);
        var ct = progress.CancellationToken;
        durationTracker.Pause();
        try
        {
            var scan = await Task.Run(() => ClipDuplicationSeeker.Find(rootA, rootB, recursive, DefaultVideoExts, progress, ct));
            ct.ThrowIfCancellationRequested();
            if (scan.Groups.Count == 0)
            {
                CompleteEmbeddedOperation(scan.Errors.Count > 0 ? "Scan incomplete" : "No cross-directory duplicates found");
                MessageBox.Show(this, (scan.Errors.Count > 0 ? "Scan incomplete; unreadable paths remain." : "Scan complete; no exact copies spanning A and B.") +
                    ClipIdentifier.FormatErrors(scan.Errors), "Seek duplications");
                return;
            }
            using var review = new DuplicationReviewForm(scan);
            if (review.ShowDialog(this) != DialogResult.Yes) { CompleteEmbeddedOperation("Review cancelled — no files changed"); return; }
            var result = await Task.Run(() => ClipDuplicationSeeker.Apply(scan, review.SelectedPaths, progress, ct));
            CompleteEmbeddedOperation(result.Cancelled ? "Cancelled — some recycling may have completed" : "Done");
            MessageBox.Show(this, result.Summary, "Seek duplications");
        }
        catch (OperationCanceledException) { CompleteEmbeddedOperation("Scan cancelled — no files changed"); }
        catch (Exception ex) { CompleteEmbeddedOperation("Failed"); MessageBox.Show(this, ex.Message, "Seek duplications", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { durationTracker.Resume(); SetButtonsEnabled(true); UpdateVideoCount(); }
    }
}
