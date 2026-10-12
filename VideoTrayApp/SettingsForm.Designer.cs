namespace VideoTrayApp;

partial class SettingsForm
{
    private ListBox lstFolders = null!;
    private Button btnRemove = null!;

    private void InitializeComponent()
    {
        SuspendLayout();
        var title = new Label
        {
            Text = "Watch folders", Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            AutoSize = true, Location = new Point(20, 20)
        };
        var description = new Label
        {
            Text = "Track clip durations in each folder and its immediate subfolders.",
            AutoSize = true, Location = new Point(20, 58)
        };
        lstFolders = new ListBox
        {
            Location = new Point(20, 90), Size = new Size(560, 200),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White,
            Font = new Font("Segoe UI", 10F), HorizontalScrollbar = true, IntegralHeight = false
        };
        lstFolders.SelectedIndexChanged += (_, _) => UpdateRemoveButton();
        var add = MakeButton("Add folder...", new Point(20, 305), 120);
        add.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        add.Click += btnAdd_Click;
        btnRemove = MakeButton("Remove", new Point(150, 305), 100);
        btnRemove.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        btnRemove.Click += btnRemove_Click;
        var save = MakeButton("Save", new Point(370, 365), 100);
        save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        save.BackColor = Color.FromArgb(0, 176, 240);
        save.Click += btnSave_Click;
        var cancel = MakeButton("Cancel", new Point(480, 365), 100);
        cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        cancel.DialogResult = DialogResult.Cancel;
        var hint = new Label
        {
            Text = "Remove all folders to turn off tracking. Unavailable folders are kept.",
            Location = new Point(20, 342), AutoSize = true,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(600, 420);
        MinimumSize = new Size(616, 459);
        BackColor = Color.FromArgb(45, 45, 48);
        ForeColor = Color.White;
        Controls.AddRange([title, description, lstFolders, add, btnRemove, hint, save, cancel]);
        AcceptButton = save;
        CancelButton = cancel;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Duration tracking - Watch folders";
        ResumeLayout(false);
        PerformLayout();
    }

    private static Button MakeButton(string text, Point location, int width) => new()
    {
        Text = text, Location = location, Size = new Size(width, 32),
        BackColor = Color.FromArgb(100, 100, 100), ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand
    };
}
