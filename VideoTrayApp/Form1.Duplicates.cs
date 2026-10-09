namespace VideoTrayApp;

public partial class Form1
{
    private async Task RunRemoveDuplicatesAsync()
    {
        if (await TryRunDefaultPresetAsync(PresetAction.RemoveDuplicates)) return;
        if (operationCts is not null)
            return;
        ShowWindow(this, EventArgs.Empty);
        using var picker = new FolderBrowserDialog
        {
            Description = "Select a folder to scan for duplicate clips. Subfolders are included.",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(workingFolderPath) ? workingFolderPath : string.Empty
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;

        SetButtonsEnabled(false);
        BeginEmbeddedOperation("Remove duplicates");
        var progress = new EmbeddedOperationProgress(this);
        var ct = progress.CancellationToken;
        try
        {
            var result = await Task.Run(() => ClipDuplicateCleaner.Find(picker.SelectedPath,
                DefaultVideoExts, progress, ct), ct);
            ct.ThrowIfCancellationRequested();
            if (result.DuplicateCount == 0)
            {
                CompleteEmbeddedOperation("No duplicate clips found");
                MessageBox.Show(this, "No duplicate clips found.\n\n" +
                    $"Unreadable files/folders: {result.Errors.Count}" + ClipIdentifier.FormatErrors(result.Errors),
                    "Remove duplicates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            progress.Report(1, 1, $"Found {result.DuplicateCount} duplicate clips");
            using var review = CreateDuplicateReviewDialog(result);
            if (review.ShowDialog(this) != DialogResult.Yes)
            {
                CompleteEmbeddedOperation("Cancelled - no files changed");
                return;
            }
            var summary = await Task.Run(() => ClipDuplicateCleaner.Apply(result, progress, ct));
            CompleteEmbeddedOperation(ct.IsCancellationRequested ? "Cancelled" : "Done");
            MessageBox.Show(this, summary, "Remove duplicates", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            CompleteEmbeddedOperation("Cancelled - no files changed");
        }
        catch (Exception ex)
        {
            CompleteEmbeddedOperation("Failed");
            MessageBox.Show(this, $"Remove duplicates failed:\n{ex.Message}", "Remove duplicates",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetButtonsEnabled(true);
            UpdateVideoCount();
        }
    }

    private static Form CreateDuplicateReviewDialog(DuplicateClipResult result)
    {
        var dialog = new Form
        {
            Text = "Review duplicate clips",
            ClientSize = new Size(760, 460),
            MinimumSize = new Size(620, 380),
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            Font = new Font("Segoe UI", 10F)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 12),
            Text = $"Found {result.DuplicateCount} extra copies across {result.Groups.Count} groups.\n" +
                "File size, duration, and full SHA-256 hash match in each group.\n" +
                "Keep the first path alphabetically in each group; send extra copies to the Recycle Bin.\n" +
                $"Unreadable files/folders: {result.Errors.Count}"
        }, 0, 0);
        layout.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Text = string.Join(Environment.NewLine + Environment.NewLine, result.Groups.Select(group =>
                $"KEEP: {group.Keep}" + Environment.NewLine +
                string.Join(Environment.NewLine, group.Duplicates.Select(path => $"RECYCLE: {path}")))) +
                ClipIdentifier.FormatErrors(result.Errors).Replace("\n", Environment.NewLine)
        }, 0, 1);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 12, 0, 0)
        };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(new Button
        {
            Text = $"Delete {result.DuplicateCount} duplicates", AutoSize = true, DialogResult = DialogResult.Yes
        });
        dialog.CancelButton = cancel;
        layout.Controls.Add(buttons, 0, 2);
        dialog.Controls.Add(layout);
        return dialog;
    }
}
