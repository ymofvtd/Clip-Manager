namespace VideoTrayApp;

internal sealed partial class MainShell
{
    private readonly ToolTip durationTooltips = new();

    private void BuildDurationStats()
    {
        durationStats.SuspendLayout();
        foreach (Control control in durationStats.Controls.Cast<Control>().ToArray()) control.Dispose();
        durationLabels.Clear();
        var title = DesktopTheme.Text("DURATION");
        title.Font = DesktopTheme.EmphasisFont;
        title.Tag = TextRole.Secondary;
        durationStats.Controls.Add(title);
        foreach (string root in trackingRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var label = DesktopTheme.Text("");
            label.AccessibleName = "Tracked duration: " + root;
            durationTooltips.SetToolTip(label, root + "\nIncludes videos in this root and its immediate subfolders.");
            durationLabels[root] = label;
            durationStats.Controls.Add(label);
        }
        if (durationLabels.Count == 0)
            durationStats.Controls.Add(DesktopTheme.Text("No tracked folders.\nAdd folders in Settings."));
        ResizeDurationStats();
        RefreshDurationStats();
        DesktopTheme.Apply(durationStats);
        durationStats.ResumeLayout();
    }

    private void ResizeDurationStats()
    {
        int width = Math.Max(1, navigation.Width - 28);
        durationStats.MaximumSize = new Size(width, 0);
        foreach (Label label in durationStats.Controls.OfType<Label>()) label.MaximumSize = new Size(width, 0);
    }

    private void RefreshDurationStats()
    {
        var totals = tracker.Totals;
        foreach (var (root, label) in durationLabels)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
            if (name.Length == 0) name = root;
            string duration = totals.TryGetValue(root, out var total)
                ? total.Duration is { } value ? WorkflowWorkspace.Duration(value) + (total.Complete ? "" : " (partial)") : "Unavailable"
                : "Refreshing…";
            string text = name + "\n" + duration;
            if (label.Text != text) label.Text = text;
            label.AccessibleDescription = root + "; " + duration + "; root and immediate subfolders";
        }
    }
}
