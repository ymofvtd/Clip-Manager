namespace VideoTrayApp;

internal sealed class TranscriptionWorkspace : UserControl
{
    private readonly MainShell shell;
    private readonly RadioButton oneClip = new() { Text = "One clip", AutoSize = true, Checked = true };
    private readonly RadioButton folderMode = new() { Text = "Folder", AutoSize = true };
    private readonly CheckBox recursive = new() { Text = "Include subfolders", AutoSize = true };
    private readonly PathPicker clip, folder;
    private readonly DesktopButton start;
    private readonly DetailsBox result = new()
    {
        Height = 170, Dock = DockStyle.Top, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, AccessibleName = "Transcription results", Visible = false
    };

    internal TranscriptionWorkspace(MainShell shell, string workingFolder, UiPreferences preferences)
    {
        this.shell = shell;
        AccessibleName = "Transcribe clips";
        clip = new("MP4 clip", file: true);
        folder = new("Clip folder", recent: preferences.RecentPaths) { Value = workingFolder };
        start = DesktopTheme.Button("Transcribe", async () => await RunAsync());
        start.Tag = CommandRole.Primary;

        var fields = DesktopTheme.Stack();
        fields.Padding = new Padding(24, 12, 24, 12);
        DesktopTheme.Add(fields, DesktopTheme.Text("Save a timestamped .transcript.txt file beside each MP4. Older plain-text transcripts get a separate .transcript.timestamped.txt copy."));
        DesktopTheme.Add(fields, DesktopTheme.Actions(oneClip, folderMode));
        DesktopTheme.Add(fields, clip);
        DesktopTheme.Add(fields, folder);
        DesktopTheme.Add(fields, recursive);
        DesktopTheme.Add(fields, DesktopTheme.Text("Transcription runs on this PC with no profanity filter. The first run with speech downloads about 149 MB of models."));
        DesktopTheme.Add(fields, DesktopTheme.Actions(start));
        DesktopTheme.Add(fields, result);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(fields);
        Controls.Add(scroll);

        folderMode.CheckedChanged += (_, _) => RefreshInputs();
        clip.ValueChanged += (_, _) => RefreshInputs();
        folder.ValueChanged += (_, _) => RefreshInputs();
        RefreshInputs();
    }

    private void RefreshInputs()
    {
        folder.Visible = recursive.Visible = folderMode.Checked;
        clip.Visible = !folderMode.Checked;
        SetBusy(shell.IsBusy);
    }

    internal void SetBusy(bool busy)
    {
        oneClip.Enabled = folderMode.Enabled = recursive.Enabled = !busy;
        clip.ReadOnly = folder.ReadOnly = busy;
        start.Enabled = !busy && (folderMode.Checked ? folder.Value.Length > 0 : VideoFiles.IsMp4(clip.Value));
    }

    internal void UseVodFolder(string path)
    {
        folder.Value = path;
        recursive.Checked = false;
        folderMode.Checked = true;
    }

    private async Task RunAsync()
    {
        if (shell.IsBusy) return;
        bool batch = folderMode.Checked;
        string source = batch ? folder.Value : clip.Value;
        bool includeSubfolders = batch && recursive.Checked;
        string context = batch ? $"Folder: {source}\nInclude subfolders: {includeSubfolders}" : $"Clip: {source}";
        result.Visible = true;
        result.Text = "Transcribing…";
        var outcome = await shell.RunJobAsync("Transcribe clips", context, false, (progress, ct) =>
            Transcription.RunAsync(source, batch, includeSubfolders, shell.DataFolder, progress, ct).GetAwaiter().GetResult());
        await UiThread.Switch(this);
        if (!IsDisposed)
            result.Text = outcome?.Details ?? "Another operation is running. Try again when it finishes.";
    }
}
