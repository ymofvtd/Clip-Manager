namespace VideoTrayApp;

internal sealed class PresetsForm : Form
{
    private readonly PresetStore store;
    private readonly string path;
    private readonly string workingFolder;
    private readonly List<Action> refreshers = [];

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
        VisibleChanged += (_, _) =>
        {
            if (!Visible) return;
            try
            {
                var latest = PresetStore.Load(path); store.Presets = latest.Presets; store.Defaults = latest.Defaults;
                foreach (var refresh in refreshers) refresh();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Presets could not be reloaded", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        DesktopTheme.Apply(this);
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
        var detail = new DetailsBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
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
        var duplicate = new Button { Text = "Duplicate", AutoSize = true };
        var rename = new Button { Text = "Rename", AutoSize = true };
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
            using var editor = new PresetEditorForm(new ActionPreset { Action = action, Target = workingFolder,
                DurationMinutes = action == PresetAction.ShuffleBatches ? 30 : 20 }, workingFolder);
            if (editor.ShowDialog(this) == DialogResult.OK && SaveChange(() => store.Presets.Add(editor.Preset)))
                RefreshList(editor.Preset.Id);
        };
        if (archiveTemplate is not null) archiveTemplate.Click += (_, _) =>
        {
            using var editor = new PresetEditorForm(new ActionPreset
            {
                Action = action, Target = workingFolder, Name = "Random 30m compilation by archives",
                DurationMinutes = 30, Selection = BatchSelection.Archives
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
        duplicate.Click += (_, _) =>
        {
            if (Selected() is not { } preset) return;
            var copy = preset.Clone(); copy.Id = Guid.NewGuid().ToString("N"); copy.Name += " copy";
            if (SaveChange(() => store.Presets.Add(copy))) RefreshList(copy.Id);
        };
        rename.Click += (_, _) =>
        {
            if (Selected() is not { } preset) return;
            using var dialog = new Form { Text = "Rename preset", ClientSize = new Size(420, 140), StartPosition = FormStartPosition.CenterParent, Font = Font };
            var input = new TextBox { Text = preset.Name, Width = 365, AccessibleName = "Preset name" };
            var save = new Button { Text = "Save name", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var layout = DesktopTheme.Stack(); DesktopTheme.Add(layout, input); DesktopTheme.Add(layout, DesktopTheme.Actions(save, cancel)); dialog.Controls.Add(layout);
            dialog.AcceptButton = save; dialog.CancelButton = cancel; DesktopTheme.Apply(dialog);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (string.IsNullOrWhiteSpace(input.Text)) { MessageBox.Show(this, "Enter a preset name."); return; }
            if (SaveChange(() => preset.Name = input.Text.Trim())) RefreshList(preset.Id);
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
            edit.Enabled = delete.Enabled = duplicate.Enabled = rename.Enabled = selected is not null;
            detail.Text = selected?.Summary(workingFolder) ?? "Always ask (Current)\r\n\r\nOpen the normal configuration workspace. Select this and click Set as default to remove the saved default.";
        };
        buttons.Controls.AddRange([add, edit, duplicate, rename, delete, setDefault]);
        if (archiveTemplate is not null) buttons.Controls.Add(archiveTemplate);
        layout.Controls.Add(list, 0, 0);
        layout.Controls.Add(detail, 1, 0);
        layout.Controls.Add(buttons, 0, 1);
        layout.SetColumnSpan(buttons, 2);
        page.Controls.Add(layout);
        RefreshList();
        refreshers.Add(() => RefreshList(Selected()?.Id));
        return page;
    }
}

internal sealed class PresetEditorForm : Form
{
    internal ActionPreset Preset { get; }
    internal PresetEditorForm(ActionPreset preset, string workingFolder)
    {
        string originalValues = System.Text.Json.JsonSerializer.Serialize(preset);
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
            control.AccessibleName = label;
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
            var values = Enum.GetValues<T>();
            var control = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = label };
            control.Items.AddRange(values.Select(v => (object)(v.ToString() switch
            { "NumberedMp4" => "Numbered MP4 (top level)", "AllVideos" => "All MP4 files (top level)", "Archives" => "Archives (MP4, recursive)", "Recycle" => "Send to Recycle Bin", _ => v.ToString() })).ToArray());
            control.SelectedIndex = Array.IndexOf(values, value);
            Add(label, control);
            saveValues.Add(() => setter(values[control.SelectedIndex]));
        }
        void PathField(string label, string value, Action<string> setter, bool file = false)
        {
            var picker = new PathPicker(label, file) { Value = value };
            Add(label, picker);
            saveValues.Add(() => setter(picker.Value));
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
            PathField(preset.Action == PresetAction.PrepareBatch ? "Destination parent folder" : "Destination folder", preset.Destination, v => preset.Destination = v);
        if (preset.Action == PresetAction.Archive)
        {
            var roots = new SourceFolderList { Paths = preset.ArchiveSources };
            Add("Archive sources", roots);
            saveValues.Add(() => preset.ArchiveSources = roots.Paths);
            Add("Source selection", new Label { AutoSize = true, Text = "With current working folder selected, add it to this list.\nOtherwise use this list, or the fixed target when the list is empty.\nOnly files directly inside each source; run once; keep completed copies." });
            Choice("Speed", preset.ArchiveSpeed, v => preset.ArchiveSpeed = v);
            Number("Slow interval (seconds)", preset.ArchiveIntervalSeconds, 0, 86400, v => preset.ArchiveIntervalSeconds = v);
            Number("I/O cap MB/s (0 = off)", preset.ArchiveMegabytesPerSecond ?? 0, 0, 10000,
                v => preset.ArchiveMegabytesPerSecond = v == 0 ? null : v);
            Number("Settling (seconds)", preset.ArchiveSettlingSeconds, 0, 3600, v => preset.ArchiveSettlingSeconds = v);
        }
        if (preset.Action == PresetAction.SeekDuplications)
        {
            PathField("Directory B", preset.DirectoryB, v => preset.DirectoryB = v);
            var recursive = new CheckBox { Text = "Include subfolders", Checked = preset.IncludeSubfolders, AutoSize = true };
            Add("Scan options", recursive); saveValues.Add(() => preset.IncludeSubfolders = recursive.Checked);
            Add("Review", new Label { Text = "Always review individual copies and confirm selection.\nAll files start as Keep; neither folder has priority.", AutoSize = true });
        }
        if (preset.Action == PresetAction.PrepareBatch)
        {
            TextField("New batch folder name", preset.BatchFolderName, v => preset.BatchFolderName = v);
            Add("Batch destination", new Label { AutoSize = true, Text = "Automatically use the highest numbered c_ folder\nin the destination. If none exists, create a new folder.\nLeave the name blank for batch_1, batch_2, etc.\nThe c_ folder does not need to exist when you save." });
            Number("Duration (minutes)", preset.DurationMinutes, 1, 10080, v => preset.DurationMinutes = v);
            Choice("Select clips", preset.Selection, v => preset.Selection = v);
            Add("Selection help", new Label { AutoSize = true, Text = "Numbered MP4: numbered top-level MP4 files.\nAll MP4 files: all top-level MP4 files.\nArchives: MP4 files in target and subfolders, excluding Backup.\nEach mode uses a random order." });
            Add("Batch naming", new Label { AutoSize = true, Text = "New clips: 1, 2, 3, ... from shortest to longest.\nExisting filenames stay unchanged; occupied numbers are skipped." });
        }
        if (preset.Action == PresetAction.ShuffleBatches)
        {
            Number("Batch limit (minutes)", preset.DurationMinutes, 1, 10080, v => preset.DurationMinutes = v);
            Add("Shuffle Batches", new Label { AutoSize = true, Text = "Mix videos from all target subfolders and loose root clips.\nExclude Backup and links. Reuse folders and add batches as needed.\nClips longer than the limit get their own folders.\nNumber clips from shortest to longest within each batch.\nNo backups or duplicate deletion." });
        }
        if (preset.Action is PresetAction.ShuffleAndName or PresetAction.NameByTens)
            Number("Starting number", preset.Start, 0, int.MaxValue, v => preset.Start = v);
        if (preset.Action == PresetAction.ShuffleAndName)
        {
            Number("Padding width", preset.Padding, 0, 64, v => preset.Padding = v);
            TextField("Filename prefix", preset.Prefix, v => preset.Prefix = v);
        }
        if (preset.Action == PresetAction.ShuffleRandom)
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
                preset.ValidateForSave();
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
        DesktopTheme.Apply(this);
        FormClosing += (_, e) =>
        {
            if (DialogResult == DialogResult.OK) return;
            foreach (var setter in saveValues) setter();
            if (System.Text.Json.JsonSerializer.Serialize(preset) != originalValues &&
                MessageBox.Show(this, "Discard the unsaved preset changes?", "Unsaved preset", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) e.Cancel = true;
        };
    }
}
