using System.Diagnostics;

namespace VideoTrayApp;

internal sealed class DuplicationReviewForm : Form
{
    private readonly DataGridView table = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false };
    internal HashSet<string> SelectedPaths { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    internal DuplicationReviewForm(DuplicationScan scan)
    {
        Text = "Seek duplications — review individual copies"; Font = new Font("Segoe UI", 10);
        ClientSize = new Size(1150, 620); MinimumSize = new Size(850, 480); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, Text = $"{scan.Groups.Count} exact-identity groups spanning A and B. All copies start as Keep; neither side has priority.\n" +
            $"Mark individual copies Recycle. Keep at least one per group. Hard links share one underlying file and stay kept.\nUnreadable files/folders: {scan.Errors.Count}" }, 0, 0);
        table.Columns.Add(new DataGridViewComboBoxColumn { Name = "Action", HeaderText = "Action", DataSource = new[] { "Keep", "Recycle" } });
        foreach (string name in new[] { "Group", "Side", "Full path", "Relative path", "Source folder", "Filename", "Bytes", "Duration", "Last write", "Underlying file" })
            table.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, ReadOnly = true });
        foreach (string name in new[] { "Full path", "Relative path", "Source folder", "Filename" })
        {
            table.Columns[name]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            table.Columns[name]!.Width = name == "Full path" ? 300 : 160;
        }
        for (int i = 0; i < 3; i++) table.Columns[i].Frozen = true;
        int groupNumber = 0;
        foreach (var group in scan.Groups)
        {
            groupNumber++;
            foreach (var copy in group.Copies)
            {
                int index = table.Rows.Add("Keep", groupNumber, copy.Side, copy.Path, copy.RelativePath, copy.Root,
                    Path.GetFileName(copy.Path), copy.Size, copy.Duration, copy.LastWriteTime.ToLocalTime(),
                    copy.HardLink ? $"Hard link: {copy.Physical.Volume:X}:{copy.Physical.Index:X} ({copy.Physical.Links} aliases)" : "Separate file");
                table.Rows[index].Tag = copy;
                if (copy.HardLink) table.Rows[index].Cells[0].ReadOnly = true;
            }
        }
        table.CurrentCellDirtyStateChanged += (_, _) => { if (table.IsCurrentCellDirty) table.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        layout.Controls.Add(table, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        void Add(string text, Action<DuplicationCopy> action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (_, _) =>
            {
                if (table.CurrentRow?.Tag is not DuplicationCopy copy) return;
                try { action(copy); } catch (Exception ex) { MessageBox.Show(this, ex.Message, text); }
            };
            buttons.Controls.Add(button);
        }
        Add("Open clip", copy => Process.Start(new ProcessStartInfo(copy.Path) { UseShellExecute = true }));
        Add("Open containing folder", copy => Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + copy.Path + "\"", UseShellExecute = true }));
        Add("Copy path", copy => Clipboard.SetText(copy.Path));
        var recycle = new Button { Text = "Recycle selected copies", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        recycle.Click += (_, _) =>
        {
            table.EndEdit();
            var selected = table.Rows.Cast<DataGridViewRow>().Where(row => Equals(row.Cells[0].Value, "Recycle"))
                .Select(row => (DuplicationCopy)row.Tag!).ToList();
            if (selected.Count == 0) { MessageBox.Show(this, "Select at least one copy to recycle.", "Review selection"); return; }
            var paths = selected.Select(copy => copy.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (scan.Groups.Any(group => group.Copies.All(copy => paths.Contains(copy.Path))))
            { MessageBox.Show(this, "Keep at least one file in every group.", "Review selection"); return; }
            using var confirmation = new Form { Text = "Confirm selected copies", ClientSize = new Size(850, 450), StartPosition = FormStartPosition.CenterParent, Font = Font };
            var confirmLayout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
            confirmLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); confirmLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); confirmLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            confirmLayout.Controls.Add(new Label { AutoSize = true, Text = $"Recycle exactly {selected.Count} selected paths; reclaimable bytes: {selected.Sum(copy => copy.Size):N0}.\n" +
                "Every kept and selected copy will be verified again. Cancellation leaves earlier successful recycling completed." }, 0, 0);
            confirmLayout.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both,
                Text = string.Join(Environment.NewLine, selected.Select(copy => copy.Path)) }, 0, 1);
            var confirmButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            confirmButtons.Controls.Add(new Button { Text = "Recycle these copies", AutoSize = true, DialogResult = DialogResult.Yes });
            confirmButtons.Controls.Add(new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel });
            confirmLayout.Controls.Add(confirmButtons, 0, 2); confirmation.Controls.Add(confirmLayout);
            if (confirmation.ShowDialog(this) != DialogResult.Yes) return;
            SelectedPaths = paths; DialogResult = DialogResult.Yes;
        };
        buttons.Controls.AddRange([recycle, cancel]); CancelButton = cancel;
        layout.Controls.Add(buttons, 0, 2); Controls.Add(layout);
    }
}
