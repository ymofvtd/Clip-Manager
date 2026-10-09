namespace VideoTrayApp;

public partial class Form1
{
    private async Task RunIdentifyClipAsync()
    {
        if (await TryRunDefaultPresetAsync(PresetAction.IdentifyClip)) return;
        if (operationCts is not null)
            return;
        ShowWindow(this, EventArgs.Empty);
        using var clipPicker = new OpenFileDialog
        {
            Title = "Identify clip - select a reference clip",
            Filter = "Video files|" + string.Join(";", DefaultVideoExts.OrderBy(ext => ext).Select(ext => "*" + ext)),
            Multiselect = false,
            CheckFileExists = true
        };
        if (clipPicker.ShowDialog(this) != DialogResult.OK)
            return;
        using var folderPicker = new FolderBrowserDialog
        {
            Description = "Where should Identify clip look? Subfolders are included.",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(workingFolderPath) ? workingFolderPath : string.Empty
        };
        if (folderPicker.ShowDialog(this) != DialogResult.OK)
            return;

        SetButtonsEnabled(false);
        BeginEmbeddedOperation("Identify clip");
        var progress = new EmbeddedOperationProgress(this);
        var ct = progress.CancellationToken;
        try
        {
            var result = await Task.Run(() => ClipIdentifier.Find(clipPicker.FileName,
                folderPicker.SelectedPath, DefaultVideoExts, progress, ct), ct);
            ct.ThrowIfCancellationRequested();
            if (result.Matches.Count == 0)
            {
                CompleteEmbeddedOperation("No matching clips found");
                MessageBox.Show(this, "No matching clips found. The reference clip is excluded.\n\n" +
                    $"Unreadable files/folders: {result.Errors.Count}" + ClipIdentifier.FormatErrors(result.Errors),
                    "Identify clip", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            progress.Report(1, 1, $"Found {result.Matches.Count} matching clips");
            using var choice = CreateIdentifyActionDialog(result);
            var action = choice.ShowDialog(this);
            if (action != DialogResult.Yes && action != DialogResult.No)
            {
                CompleteEmbeddedOperation("Cancelled - no files changed");
                return;
            }
            string? destination = null;
            if (action == DialogResult.No)
            {
                using var destinationPicker = new FolderBrowserDialog
                {
                    Description = "Select a folder to move the matching clips to",
                    UseDescriptionForTitle = true
                };
                if (destinationPicker.ShowDialog(this) != DialogResult.OK)
                {
                    CompleteEmbeddedOperation("Cancelled - no files changed");
                    return;
                }
                destination = destinationPicker.SelectedPath;
            }
            var summary = await Task.Run(() => ClipIdentifier.Apply(result, clipPicker.FileName,
                destination, progress, ct));
            CompleteEmbeddedOperation(ct.IsCancellationRequested ? "Cancelled" : "Done");
            MessageBox.Show(this, summary, "Identify clip", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            CompleteEmbeddedOperation("Cancelled - no files changed");
        }
        catch (Exception ex)
        {
            CompleteEmbeddedOperation("Failed");
            MessageBox.Show(this, $"Identify clip failed:\n{ex.Message}", "Identify clip",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetButtonsEnabled(true);
            UpdateVideoCount();
        }
    }

    private static Form CreateIdentifyActionDialog(ClipSearchResult result)
    {
        var dialog = new Form
        {
            Text = "Identify clip",
            ClientSize = new Size(680, 420),
            MinimumSize = new Size(580, 360),
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            Font = new Font("Segoe UI", 10F)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 0, 0, 12),
            Text = $"Found {result.Matches.Count} clips with the same hash and duration.\n" +
                "Do you want to delete them or move them to a different folder?\n" +
                "Delete sends matches to the Recycle Bin. Your reference clip is excluded.\n" +
                $"Unreadable files/folders: {result.Errors.Count}"
        };
        var paths = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Text = string.Join(Environment.NewLine, result.Matches) +
                ClipIdentifier.FormatErrors(result.Errors).Replace("\n", Environment.NewLine)
        };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 12, 0, 0)
        };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var move = new Button { Text = "Move to folder...", AutoSize = true, DialogResult = DialogResult.No };
        var delete = new Button { Text = "Delete matches", AutoSize = true, DialogResult = DialogResult.Yes };
        buttons.Controls.AddRange([cancel, move, delete]);
        dialog.CancelButton = cancel;
        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(paths, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        dialog.Controls.Add(layout);
        return dialog;
    }
}
