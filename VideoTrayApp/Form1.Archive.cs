namespace VideoTrayApp;

public partial class Form1
{
    private readonly OperationCoordinator operationCoordinator = new();
    private IDisposable? foregroundLease;
    private ArchiveWorker? archiveWorker;
    private ArchiveForm? archiveForm;
    private ArchiveSettings archiveSettings = new();

    private void InitializeArchive()
    {
        archiveWorker = new(operationCoordinator, DefaultVideoExts, AppDataFolder);
        archiveWorker.StatusChanged += status =>
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke(() =>
            {
                if (trayIcon is null) return;
                string text = status.Warning.Length > 0 || status.UnavailableRoots.Length > 0 || status.Failed > 0
                    ? "Clips Manager — Archive needs attention" : status.Running
                    ? $"Clips Manager — Archive {(status.Paused ? "paused" : "running")}, queued {status.Queued}" : BuildTrayTooltip();
                trayIcon.Text = text.Length > 63 ? text[..63] : text;
            }); }
            catch (InvalidOperationException) { /* Window handle may be closing. */ }
        };
        try
        {
            archiveSettings = ArchiveSettings.Load(AppDataFolder);
            if (archiveSettings.WatchContinuously) archiveWorker.Start(archiveSettings);
        }
        catch (Exception ex)
        {
            lblProgressStatus.Text = "Archive settings need attention";
            MessageBox.Show(this, ex.Message, "Archive could not resume", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    private async Task RunArchiveAsync()
    {
        if (archiveWorker is null) return;
        if (!archiveWorker.Running && await TryRunDefaultPresetAsync(PresetAction.Archive)) return;
        if (archiveForm is { IsDisposed: false }) { archiveForm.Show(); archiveForm.BringToFront(); return; }
        try
        {
            archiveSettings = archiveWorker.Running ? archiveWorker.Settings.Clone() : ArchiveSettings.Load(AppDataFolder);
            archiveForm = new ArchiveForm(archiveWorker, archiveSettings, AppDataFolder);
            archiveForm.Show(this);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Archive", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    private void RunArchivePreset(ActionPreset preset)
    {
        if (archiveWorker is null) return;
        if (archiveWorker.Running)
        {
            MessageBox.Show(this, "Stop the current Archive run before starting a preset.", "Archive");
            return;
        }
        archiveSettings = preset.ArchiveConfiguration(workingFolderPath);
        archiveSettings.Save(AppDataFolder);
        archiveWorker.Start(archiveSettings);
        if (archiveForm is { IsDisposed: false }) archiveForm.Close();
        archiveForm = new ArchiveForm(archiveWorker, archiveSettings, AppDataFolder);
        archiveForm.Show(this);
    }
}
