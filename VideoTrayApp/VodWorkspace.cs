namespace VideoTrayApp;

internal sealed class VodWorkspace : UserControl
{
    private readonly PathPicker folder;
    private readonly Label status = DesktopTheme.Text("");
    private readonly Action<string> remember;
    private readonly Action<string> transcribe;

    internal VodWorkspace(string savedFolder, string workingFolder, IEnumerable<string> recent, Action<string> remember, Action<string> transcribe)
    {
        this.remember = remember;
        this.transcribe = transcribe;
        AccessibleName = "VOD workspace";
        folder = new("VOD workspace folder", recent: recent) { Value = savedFolder.Length > 0 ? savedFolder : workingFolder };
        var fields = DesktopTheme.Stack();
        fields.Padding = new Padding(24, 12, 24, 12);
        DesktopTheme.Add(fields, DesktopTheme.Text("Use the same folder for VODs, timed transcripts, clip review, and Premiere exports."));
        DesktopTheme.Add(fields, folder);
        DesktopTheme.Add(fields, DesktopTheme.Actions(
            DesktopTheme.Button("Transcribe VODs", () => Run(transcribe, "VOD folder selected for transcription.")),
            DesktopTheme.Button("Open Clip Library", () => Run(OpenLibrary, "Opening Clip Library.")),
            DesktopTheme.Button("Open Premiere exports", () => Run(OpenFinished, "Opening Finished."))));
        DesktopTheme.Add(fields, status);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(fields);
        Controls.Add(scroll);
    }

    private void Run(Action<string> action, string success)
    {
        try
        {
            if (!Path.IsPathFullyQualified(folder.Value) || !Directory.Exists(folder.Value))
                throw new DirectoryNotFoundException("Choose an available VOD workspace folder.");
            string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Value));
            if (!File.Exists(Path.Combine(path, "Clip Library", "Open Clip Library.cmd")))
                throw new FileNotFoundException("Choose the folder that contains the Clip Library launcher.");
            action(path);
            remember(path);
            status.Text = success;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            status.Text = error.Message;
        }
    }

    private static void OpenLibrary(string path)
        => DesktopTheme.Open(Path.Combine(path, "Clip Library", "Open Clip Library.cmd"));

    private static void OpenFinished(string path)
    {
        string finished = Path.Combine(path, "Finished");
        if (!Directory.Exists(finished))
            throw new DirectoryNotFoundException("The Finished folder appears after the first VOD clip report.");
        DesktopTheme.Open(finished);
    }
}
