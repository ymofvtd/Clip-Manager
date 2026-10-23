namespace VideoTrayApp;

internal sealed class ArchiveForm : Form
{
    private readonly ArchiveWorker worker;
    private readonly string dataFolder;
    private readonly ListBox sources = new() { Dock = DockStyle.Fill };
    private readonly TextBox destination = new() { Dock = DockStyle.Fill };
    private readonly CheckBox watch = new() { Text = "Watch continuously", AutoSize = true };
    private readonly CheckBox verify = new() { Text = "Recheck all content once", AutoSize = true };
    private readonly CheckBox preserveOriginalName = new() { Text = "Preserve original filename", AutoSize = true, AccessibleName = "Preserve original filename" };
    private readonly ComboBox speed = new() { DropDownStyle = ComboBoxStyle.DropDownList, DataSource = Enum.GetValues<ArchiveSpeed>(), Width = 90 };
    private readonly NumericUpDown interval = new() { Minimum = 0, Maximum = 86400, Width = 90 };
    private readonly CheckBox limit = new() { Text = "Limit I/O (MB/s)", AutoSize = true };
    private readonly NumericUpDown bandwidth = new() { Minimum = 1, Maximum = 10000, Width = 90 };
    private readonly NumericUpDown settling = new() { Minimum = 0, Maximum = 3600, Width = 90 };
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
    private readonly Button start = new() { Text = "Start", AutoSize = true };
    private readonly Button pause = new() { Text = "Pause", AutoSize = true };
    private readonly Button resume = new() { Text = "Resume", AutoSize = true };
    private readonly Button stop = new() { Text = "Stop", AutoSize = true };
    private readonly Panel configuration = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly int reconcileSeconds;

    internal ArchiveForm(ArchiveWorker worker, ArchiveSettings settings, string dataFolder)
    {
        this.worker = worker; this.dataFolder = dataFolder; reconcileSeconds = settings.ReconcileSeconds;
        Text = "Archive — verified copies"; Font = new Font("Segoe UI", 10);
        ClientSize = new Size(850, 660); MinimumSize = new Size(750, 590); StartPosition = FormStartPosition.CenterParent;
        sources.Items.AddRange(settings.Sources.Cast<object>().ToArray());
        destination.Text = settings.Destination; watch.Checked = settings.WatchContinuously;
        preserveOriginalName.Checked = settings.PreserveOriginalName;
        verify.Checked = settings.VerifyExistingContent; verify.Enabled = !watch.Checked;
        watch.CheckedChanged += (_, _) => { verify.Enabled = !watch.Checked; if (watch.Checked) verify.Checked = false; };
        speed.SelectedItem = settings.Speed; interval.Value = Math.Clamp(settings.IntervalSeconds, 0, 86400);
        settling.Value = Math.Clamp(settings.SettlingSeconds, 0, 3600);
        limit.Checked = settings.MegabytesPerSecond.HasValue; bandwidth.Value = Math.Clamp(settings.MegabytesPerSecond ?? 20, 1, 10000);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 5, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 255));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var sourcePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        sourcePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sourcePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var sourceButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, AutoSize = true };
        var add = new Button { Text = "Add folder...", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };
        add.Click += (_, _) => { using var picker = new FolderBrowserDialog(); if (picker.ShowDialog(this) == DialogResult.OK
            && !sources.Items.Cast<string>().Any(p => ClipIdentifier.SamePath(p, picker.SelectedPath))) sources.Items.Add(picker.SelectedPath); };
        remove.Click += (_, _) => { if (sources.SelectedIndex >= 0) sources.Items.RemoveAt(sources.SelectedIndex); };
        sourceButtons.Controls.AddRange([add, remove]); sourcePanel.Controls.Add(sources, 0, 0); sourcePanel.Controls.Add(sourceButtons, 1, 0);
        fields.Controls.Add(new Label { Text = "Sources (direct files only)", AutoSize = true }, 0, 0); fields.Controls.Add(sourcePanel, 1, 0);
        var destPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        destPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); destPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) => { using var picker = new FolderBrowserDialog { SelectedPath = destination.Text }; if (picker.ShowDialog(this) == DialogResult.OK) destination.Text = picker.SelectedPath; };
        destPanel.Controls.Add(destination, 0, 0); destPanel.Controls.Add(browse, 1, 0);
        fields.Controls.Add(new Label { Text = "Destination", AutoSize = true }, 0, 1); fields.Controls.Add(destPanel, 1, 1);
        var mode = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        mode.Controls.AddRange([watch, verify, new Label { Text = "Speed", AutoSize = true, Padding = new Padding(8, 5, 0, 0) }, speed]);
        fields.Controls.Add(new Label { Text = "Run once / Watch", AutoSize = true }, 0, 2); fields.Controls.Add(mode, 1, 2);
        var slow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        slow.Controls.AddRange([new Label { Text = "Interval (s)", AutoSize = true }, interval, limit, bandwidth,
            new Label { Text = "Settle (s)", AutoSize = true }, settling]);
        fields.Controls.Add(new Label { Text = "Slow / readiness", AutoSize = true }, 0, 3); fields.Controls.Add(slow, 1, 3);
        fields.Controls.Add(new Label { Text = "Filenames", AutoSize = true }, 0, 4); fields.Controls.Add(preserveOriginalName, 1, 4);
        configuration.Controls.Add(fields); layout.Controls.Add(configuration, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([start, pause, resume, stop, new Label { AutoSize = true, Padding = new Padding(5, 6, 0, 0), Text = "Closing this window keeps Archive running in the tray." }]);
        start.Click += (_, _) =>
        {
            try
            {
                var next = new ArchiveSettings { Sources = sources.Items.Cast<string>().ToList(), Destination = destination.Text.Trim(),
                    WatchContinuously = watch.Checked, Speed = (ArchiveSpeed)speed.SelectedItem!, IntervalSeconds = (int)interval.Value,
                    VerifyExistingContent = !watch.Checked && verify.Checked,
                    PreserveOriginalName = preserveOriginalName.Checked,
                    MegabytesPerSecond = limit.Checked ? (int)bandwidth.Value : null, SettlingSeconds = (int)settling.Value, ReconcileSeconds = reconcileSeconds };
                next.Validate(false); next.Save(dataFolder); worker.Start(next); RefreshStatus();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Archive settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        pause.Click += (_, _) => { worker.Pause(); RefreshStatus(); };
        resume.Click += (_, _) => { worker.Resume(); RefreshStatus(); };
        stop.Click += async (_, _) =>
        {
            stop.Enabled = false;
            await worker.StopAsync();
            try { var next = worker.Settings.Clone(); next.WatchContinuously = false; next.Save(dataFolder); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save stopped state"); }
            RefreshStatus();
        };
        layout.Controls.Add(buttons, 0, 1); layout.Controls.Add(status, 0, 2); layout.Controls.Add(progress, 0, 3); layout.Controls.Add(log, 0, 4);
        Controls.Add(layout); timer.Tick += (_, _) => RefreshStatus(); timer.Start(); RefreshStatus();
        Resize += (_, _) => status.MaximumSize = new Size(Math.Max(1, ClientSize.Width - 30), 0);
        status.MaximumSize = new Size(ClientSize.Width - 30, 0);
    }
    private void RefreshStatus()
    {
        var value = worker.Status;
        configuration.Enabled = start.Enabled = !value.Running;
        pause.Enabled = value.Running && !value.Paused; resume.Enabled = value.Running && value.Paused; stop.Enabled = value.Running;
        status.Text = $"{(value.Paused ? "Paused" : value.Running ? "Running" : "Stopped")} | Queued: {value.Queued} | Copied and verified: {value.Copied} | Already archived: {value.AlreadyArchived} | Failed/retrying: {value.Failed}\n" +
            $"Skipped unchanged (metadata checked, latest scan): {value.Unchanged}\n" +
            $"Last success: {value.LastSuccess?.ToLocalTime().ToString("g") ?? "none"}\n{value.Current}\n{value.Warning}" +
            (value.UnavailableRoots.Length > 0 ? "\nUnavailable roots: " + string.Join("; ", value.UnavailableRoots) : "");
        progress.Value = Math.Clamp(value.Percent, 0, 100);
        string text = string.Join(Environment.NewLine, value.Log);
        if (text != log.Text) { log.Text = text; log.SelectionStart = log.TextLength; log.ScrollToCaret(); }
    }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}
