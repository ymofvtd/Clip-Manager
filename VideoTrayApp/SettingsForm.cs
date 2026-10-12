namespace VideoTrayApp;

public partial class SettingsForm : Form
{
    public IReadOnlyList<string> SelectedFolderPaths { get; private set; }

    public SettingsForm(IEnumerable<string> currentFolderPaths)
    {
        InitializeComponent();
        SelectedFolderPaths = WatchFolderSettings.Normalize(currentFolderPaths);
        foreach (string path in SelectedFolderPaths) lstFolders.Items.Add(path);
        UpdateRemoveButton();
    }

    private void btnAdd_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Add a folder to watch for clip durations",
            SelectedPath = lstFolders.SelectedItem as string ?? string.Empty
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string path = WatchFolderSettings.Normalize([dialog.SelectedPath])[0];
        int existingIndex = lstFolders.Items.Cast<string>().ToList()
            .FindIndex(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
        lstFolders.SelectedIndex = existingIndex >= 0 ? existingIndex : lstFolders.Items.Add(path);
    }

    private void btnRemove_Click(object? sender, EventArgs e)
    {
        int index = lstFolders.SelectedIndex;
        if (index < 0) return;
        lstFolders.Items.RemoveAt(index);
        if (lstFolders.Items.Count > 0) lstFolders.SelectedIndex = Math.Min(index, lstFolders.Items.Count - 1);
        UpdateRemoveButton();
    }

    private void UpdateRemoveButton() => btnRemove.Enabled = lstFolders.SelectedIndex >= 0;

    private void btnSave_Click(object? sender, EventArgs e)
    {
        // Keep unavailable saved roots: disconnected drives must not silently lose their settings.
        SelectedFolderPaths = WatchFolderSettings.Normalize(lstFolders.Items.Cast<string>());
        DialogResult = DialogResult.OK;
        Close();
    }
}
