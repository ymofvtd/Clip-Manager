namespace VideoTrayApp;

internal sealed class PresetsForm : Form
{
    private readonly PresetStore store;
    private readonly string path;
    private readonly string workingFolder;

    internal PresetsForm(PresetStore store, string path, string workingFolder)
    {
        this.store = store;
        this.path = path;
        this.workingFolder = workingFolder;
        Text = "Presets";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(850, 540);
        MinimumSize = new Size(740, 450);
        Font = new Font("Segoe UI", 10);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        foreach (var action in Enum.GetValues<PresetAction>())
            tabs.TabPages.Add(BuildTab(action));
        Controls.Add(tabs);
    }

    private TabPage BuildTab(PresetAction action)
    {
        var page = new TabPage(ActionPreset.Label(action));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var list = new ListBox { Dock = DockStyle.Fill };
        var detail = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        var presets = new List<ActionPreset>();
        ActionPreset? Selected() => list.SelectedIndex > 0 && list.SelectedIndex <= presets.Count ? presets[list.SelectedIndex - 1] : null;
        void RefreshList(string? selectedId = null)
        {
            presets = store.Presets.Where(p => p.Action == action).ToList();
            list.Items.Clear();
            list.Items.Add("Always ask (Current)" + (store.DefaultFor(action) is null ? " — Default" : ""));
            foreach (var preset in presets)
                list.Items.Add(preset.Name + (store.DefaultFor(action)?.Id == preset.Id ? " — Default" : ""));
            list.SelectedIndex = selectedId is null ? 0 : Math.Max(0, presets.FindIndex(p => p.Id == selectedId) + 1);
        }
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var add = new Button { Text = "Add preset", AutoSize = true };
        var edit = new Button { Text = "Edit", AutoSize = true };
        var delete = new Button { Text = "Delete", AutoSize = true };
        var setDefault = new Button { Text = "Set as default", AutoSize = true };
        Button? archiveTemplate = action == PresetAction.PrepareBatch
            ? new Button { Text = "Add random 30m archives", AutoSize = true } : null;
        bool SaveChange(Action change)
        {
            var oldPresets = store.Presets.Select(p => p.Clone()).ToList();
            var oldDefaults = new Dictionary<PresetAction, string>(store.Defaults);
            try { change(); store.Save(path); return true; }
            catch (Exception ex)
            {
                store.Presets = oldPresets;
                store.Defaults = oldDefaults;
                MessageBox.Show(this, ex.Message, "Could not save presets", MessageBoxButtons.OK, MessageBoxIcon.Error);
                RefreshList();
                return false;
            }
        }
        add.Click += (_, _) =>
        {
            using var editor = new PresetEditorForm(new ActionPreset { Action = action, Target = workingFolder }, workingFolder);
            if (editor.ShowDialog(this) == DialogResult.OK && SaveChange(() => store.Presets.Add(editor.Preset)))
                RefreshList(editor.Preset.Id);
        };
        if (archiveTemplate is not null) archiveTemplate.Click += (_, _) =>
        {
            using var editor = new PresetEditorForm(new ActionPreset
            {
                Action = action, Target = workingFolder, Name = "Random 30m compilation by archives",
                DurationMinutes = 30, Selection = BatchSelection.Archives, Naming = BatchNaming.NumberByTens
            }, workingFolder);
            if (editor.ShowDialog(this) == DialogResult.OK && SaveChange(() => store.Presets.Add(editor.Preset)))
                RefreshList(editor.Preset.Id);
        };
        edit.Click += (_, _) =>
        {
            if (Selected() is not { } preset) return;
            using var editor = new PresetEditorForm(preset.Clone(), workingFolder);
            if (editor.ShowDialog(this) == DialogResult.OK && SaveChange(() => store.Presets[store.Presets.IndexOf(preset)] = editor.Preset))
                RefreshList(editor.Preset.Id);
        };
        delete.Click += (_, _) =>
        {
            if (Selected() is not { } preset) return;
            if (MessageBox.Show(this, $"Delete '{preset.Name}'?" + (store.DefaultFor(action)?.Id == preset.Id ? "\nThe default will return to Always ask." : ""),
                "Delete preset", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (SaveChange(() =>
            {
                store.Presets.Remove(preset);
                if (store.DefaultFor(action) is null) store.Defaults.Remove(action);
            })) RefreshList();
        };
        setDefault.Click += (_, _) =>
        {
            var selected = Selected();
            if (SaveChange(() =>
            {
                if (selected is null) store.Defaults.Remove(action);
                else store.Defaults[action] = selected.Id;
            })) RefreshList(selected?.Id);
        };
        list.SelectedIndexChanged += (_, _) =>
        {
            var selected = Selected();
            edit.Enabled = delete.Enabled = selected is not null;
            detail.Text = selected?.Summary(workingFolder) ?? "Always ask (Current)\r\n\r\nUse the existing action dialogs each time. Select this and click Set as default to restore manual prompts.";
        };
        buttons.Controls.AddRange([add, edit, delete, setDefault]);
        if (archiveTemplate is not null) buttons.Controls.Add(archiveTemplate);
        layout.Controls.Add(list, 0, 0);
        layout.Controls.Add(detail, 1, 0);
        layout.Controls.Add(buttons, 0, 1);
        layout.SetColumnSpan(buttons, 2);
        page.Controls.Add(layout);
        RefreshList();
        return page;
    }
}

internal sealed class PresetEditorForm : Form
{
    internal ActionPreset Preset { get; }
    internal PresetEditorForm(ActionPreset preset, string workingFolder)
    {
        Preset = preset;
        Text = "Edit preset — " + ActionPreset.Label(preset.Action);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 650);
        MinimumSize = new Size(600, 480);
        Font = new Font("Segoe UI", 10);
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(12) };
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 0, 16, 0) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(fields);
        var saveValues = new List<Action>();
        void Add(string label, Control control)
        {
            int row = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            fields.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, row);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(3, 6, 3, 6);
            fields.Controls.Add(control, 1, row);
        }
        TextBox TextField(string label, string value, Action<string> setter)
        {
            var control = new TextBox { Text = value };
            Add(label, control);
            saveValues.Add(() => setter(control.Text.Trim()));
            return control;
        }
        void Number(string label, int value, int min, int max, Action<int> setter)
        {
            var control = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max) };
            Add(label, control);
            saveValues.Add(() => setter((int)control.Value));
        }
        void Choice<T>(string label, T value, Action<T> setter) where T : struct, Enum
        {
            var control = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DataSource = Enum.GetValues<T>(), SelectedItem = value };
            Add(label, control);
            saveValues.Add(() => setter((T)control.SelectedItem!));
        }
        void PathField(string label, string value, Action<string> setter, bool file = false)
        {
            var panel = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var input = new TextBox { Text = value, Dock = DockStyle.Fill };
            var browse = new Button { Text = "Browse…", AutoSize = true };
            browse.Click += (_, _) =>
            {
                if (file)
                {
                    using var picker = new OpenFileDialog { Title = label, Filter = "Video files|*.mp4;*.mov;*.m4v;*.mxf;*.avi;*.mkv;*.wmv;*.mts;*.m2ts;*.mpg;*.mpeg;*.3gp;*.flv;*.webm", CheckFileExists = true };
                    if (picker.ShowDialog(this) == DialogResult.OK) input.Text = picker.FileName;
                }
                else
                {
                    using var picker = new FolderBrowserDialog { Description = label, UseDescriptionForTitle = true, SelectedPath = input.Text };
                    if (picker.ShowDialog(this) == DialogResult.OK) input.Text = picker.SelectedPath;
                }
            };
            panel.Controls.Add(input, 0, 0);
            panel.Controls.Add(browse, 1, 0);
            Add(label, panel);
            saveValues.Add(() => setter(input.Text.Trim()));
        }
        TextField("Preset name", preset.Name, v => preset.Name = v);
        var useWorking = new CheckBox { Text = "Use current working folder", Checked = preset.UseWorkingFolder, AutoSize = true };
        Add("Target", useWorking);
        saveValues.Add(() => preset.UseWorkingFolder = useWorking.Checked);
        PathField("Fixed target folder", preset.Target, v => preset.Target = v);
        var targetPanel = fields.GetControlFromPosition(1, fields.RowCount - 1)!;
        targetPanel.Enabled = !useWorking.Checked;
        useWorking.CheckedChanged += (_, _) => targetPanel.Enabled = !useWorking.Checked;
        if (preset.Action is PresetAction.PrepareBatch or PresetAction.Archive or PresetAction.IdentifyClip)
            PathField("Destination folder", preset.Destination, v => preset.Destination = v);
        if (preset.Action == PresetAction.PrepareBatch)
        {
            var create = new CheckBox { Text = "Create a new batch subfolder in destination", Checked = preset.CreateBatchFolder, AutoSize = true };
            Add("Batch destination", create);
            saveValues.Add(() => preset.CreateBatchFolder = create.Checked);
            Number("Duration (minutes)", preset.DurationMinutes, 1, 10080, v => preset.DurationMinutes = v);
            Choice("Select clips", preset.Selection, v => preset.Selection = v);
            Add("Selection help", new Label { AutoSize = true, Text = "NumberedMp4: existing batch behavior.\nAllVideos: all top-level videos.\nArchives: videos in target and subfolders, excluding Backup.\nEach mode uses a random order." });
            Choice("Batch naming", preset.Naming, v => preset.Naming = v);
        }
        if (preset.Action is PresetAction.PrepareBatch or PresetAction.ShuffleAndName or PresetAction.NameByTens)
            Number("Starting number", preset.Start, 0, int.MaxValue, v => preset.Start = v);
        if (preset.Action is PresetAction.PrepareBatch or PresetAction.ShuffleAndName)
        {
            Number("Padding width", preset.Padding, 0, 64, v => preset.Padding = v);
            TextField("Filename prefix", preset.Prefix, v => preset.Prefix = v);
        }
        if (preset.Action is PresetAction.PrepareBatch or PresetAction.ShuffleRandom)
            Number("Random name length", preset.RandomLength, 4, 64, v => preset.RandomLength = v);
        if (preset.Action == PresetAction.IdentifyClip)
        {
            PathField("Reference clip", preset.ReferenceClip, v => preset.ReferenceClip = v, file: true);
            Choice("Matching clips", preset.IdentifyBehavior, v => preset.IdentifyBehavior = v);
        }
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "Save preset", AutoSize = true };
        save.Click += (_, _) =>
        {
            try
            {
                foreach (var setter in saveValues) setter();
                preset.Validate(workingFolder);
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Check preset settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        buttons.Controls.AddRange([cancel, save]);
        outer.Controls.Add(scroll, 0, 0);
        outer.Controls.Add(buttons, 0, 1);
        Controls.Add(outer);
        AcceptButton = save;
        CancelButton = cancel;
    }
}
