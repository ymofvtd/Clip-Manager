using Microsoft.VisualBasic;

namespace VideoTrayApp;

public partial class Form1
{
    private async Task RunShuffleBatchesAsync()
    {
        if (await TryRunDefaultPresetAsync(PresetAction.ShuffleBatches)) return;
        if (operationCts is not null) return;
        ShowWindow(this, EventArgs.Empty);
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose the archive root. All clip subfolders and loose root clips will be reshuffled.",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(workingFolderPath) ? workingFolderPath : string.Empty
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        string input = Interaction.InputBox("Maximum minutes per batch (clips longer than this get their own folders):",
            "Shuffle Batches", "30");
        if (string.IsNullOrWhiteSpace(input)) return;
        if (!int.TryParse(input, out int minutes) || minutes is < 1 or > 10080)
        {
            MessageBox.Show(this, "Enter a whole number from 1 to 10080 minutes.", "Shuffle Batches",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // Use the same confirmation, watcher pause, and rollback as saved presets.
        await RunPresetAsync(new ActionPreset
        {
            Name = "Reshuffle the entire archive", Action = PresetAction.ShuffleBatches,
            UseWorkingFolder = false, Target = picker.SelectedPath, DurationMinutes = minutes
        });
    }
}
