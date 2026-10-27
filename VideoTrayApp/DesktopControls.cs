using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace VideoTrayApp;

internal static class DesktopTheme
{
    internal static readonly Font BodyFont = new("Segoe UI", 10.5f);
    internal static readonly Font EmphasisFont = new("Segoe UI", 10.5f, FontStyle.Bold);
    internal static AppTheme Current { get; set; }
    internal static bool Dark => Current == AppTheme.Dark || Current == AppTheme.System && SystemDark();
    private static bool SystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
    internal static Color Canvas => Tone("#F5F6FC", "#11172B");
    internal static Color Surface => Tone("#FFFFFF", "#1B2440");
    internal static Color Inset => Tone("#EDF0FA", "#151E36");
    internal static Color Ink => Tone("#222A45", "#EDF0FF");
    internal static Color Muted => Tone("#626C86", "#A8B4D4");
    internal static Color Border => Tone("#D4DAEB", "#3C4A70");
    internal static Color Accent => Tone("#6550D9", "#A99AFF");
    internal static Color AccentFill => ColorTranslator.FromHtml("#6550D9");
    internal static Color AccentSoft => Tone("#EAE5FF", "#332E58");
    internal static Color Hover => Tone("#F0EDFF", "#2A3556");
    private static Color Tone(string light, string dark) => ColorTranslator.FromHtml(Dark ? dark : light);
    internal static void Apply(Control root)
    {
        bool contrast = SystemInformation.HighContrast;
        Color surface = contrast ? SystemColors.Window : Surface;
        Color text = contrast ? SystemColors.WindowText : Ink;
        Color border = contrast ? SystemColors.WindowText : Border;
        var controls = new List<Control>();
        void Collect(Control control) { controls.Add(control); control.SuspendLayout(); foreach (Control child in control.Controls) Collect(child); }
        Collect(root);
        void Style(Control control)
        {
            control.BackColor = contrast ? SystemColors.Window : control.Tag is SurfaceRole area ? area switch
            {
                SurfaceRole.Surface => Surface,
                SurfaceRole.Inset or SurfaceRole.Sidebar => Inset,
                _ => Canvas
            } : control is TabPage ? Canvas
                : control is TextBoxBase or ListBox or ComboBox or NumericUpDown or DataGridView ? surface
                : control.Parent?.BackColor ?? Canvas;
            control.ForeColor = !contrast && control.Tag is TextRole textRole
                ? textRole == TextRole.Accent ? Accent : Muted : text;
            // Native TabControl ignores BackColor. Give pages their own painted surface
            // so descendants never inherit its light system background in Dark mode.
            if (control is TabPage page) page.UseVisualStyleBackColor = false;
            if (control is Button button)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = border;
                button.FlatAppearance.BorderSize = 1;
                button.FlatAppearance.MouseOverBackColor = contrast ? SystemColors.Highlight : Hover;
                button.MinimumSize = new Size(0, 36);
                button.Padding = new Padding(12, 5, 12, 5);
                if (!contrast && button.Tag is CommandRole role)
                {
                    button.BackColor = role == CommandRole.Primary ? AccentFill : ColorTranslator.FromHtml("#B42350");
                    button.ForeColor = Color.White;
                }
                button.Invalidate();
            }
            if (control is DataGridView grid)
            {
                grid.BackgroundColor = surface; grid.GridColor = border; grid.EnableHeadersVisualStyles = false;
                grid.DefaultCellStyle.BackColor = grid.ColumnHeadersDefaultCellStyle.BackColor = surface;
                grid.DefaultCellStyle.ForeColor = grid.ColumnHeadersDefaultCellStyle.ForeColor = text;
                grid.ColumnHeadersDefaultCellStyle.BackColor = contrast ? surface : Inset;
                grid.ColumnHeadersDefaultCellStyle.Font = EmphasisFont;
                grid.AlternatingRowsDefaultCellStyle.BackColor = contrast ? surface : Canvas;
                grid.DefaultCellStyle.SelectionBackColor = contrast ? SystemColors.Highlight : AccentSoft;
                grid.DefaultCellStyle.SelectionForeColor = contrast ? SystemColors.HighlightText : Ink;
                grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
                grid.RowTemplate.Height = 32;
                grid.BorderStyle = BorderStyle.None;
            }
            if (control.ContextMenuStrip is { } menu) StyleMenu(menu);
            foreach (Control child in control.Controls) Style(child);
        }
        Style(root);
        if (root is Form) root.BackColor = contrast ? SystemColors.Control : Canvas;
        for (int i = controls.Count - 1; i >= 0; i--) controls[i].ResumeLayout(false);
        root.PerformLayout();
    }
    internal static void StyleMenu(ContextMenuStrip menu)
    {
        menu.RenderMode = ToolStripRenderMode.System;
        menu.BackColor = SystemInformation.HighContrast ? SystemColors.Menu : Surface;
        menu.ForeColor = SystemInformation.HighContrast ? SystemColors.MenuText : Ink;
    }
    internal static Label Text(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(760, 0), Margin = new Padding(0, 8, 0, 8), UseMnemonic = false };
    internal static DesktopButton Button(string text, Action click)
    {
        var button = new DesktopButton { Text = text.Replace(" & ", " && "), AutoSize = true, AccessibleName = text.Replace(" & ", " and ").Replace("&", ""), Margin = new Padding(0, 4, 8, 4),
            MinimumSize = new Size(0, 36), Padding = new Padding(12, 5, 12, 5), FlatStyle = FlatStyle.Flat, Font = BodyFont };
        button.Click += (_, _) => click();
        return button;
    }
    internal static TableLayoutPanel Stack() => new ResponsiveStack();
    internal static void Add(TableLayoutPanel stack, Control control)
    {
        int row = stack.RowCount++; stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Top; stack.Controls.Add(control, 0, row);
    }
    internal static FlowLayoutPanel Actions(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
        panel.Controls.AddRange(controls); return panel;
    }
    internal static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    internal static string StateLabel(Enum value) => System.Text.RegularExpressions.Regex.Replace(value.ToString(), @"\B[A-Z]", " $0");
}
internal enum CommandRole { Primary, Recycle }
internal enum SurfaceRole { Canvas, Surface, Inset, Sidebar }
internal enum TextRole { Secondary, Accent }
internal enum ButtonKind { Command, Navigation, Tool, Tab }

// Keep Button's keyboard, mnemonic, dialog and accessibility behavior; only replace its paint.
internal sealed class DesktopButton : Button
{
    private bool hovered, pressed, selected;
    private string statusText = "";
    private bool statusActive;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string StatusText
    {
        get => statusText;
        set { if (statusText == value) return; statusText = value; UpdateDescription(); Invalidate(); }
    }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool StatusActive
    {
        get => statusActive;
        set { if (statusActive == value) return; statusActive = value; Invalidate(); }
    }
    private void UpdateDescription() => AccessibleDescription = string.Join("; ",
        new[] { selected ? "Selected" : "", statusText }.Where(value => value.Length > 0));
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal ButtonKind Kind { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Selected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value; Font = value ? DesktopTheme.EmphasisFont : DesktopTheme.BodyFont;
            UpdateDescription();
            Invalidate();
        }
    }
    internal DesktopButton() => DoubleBuffered = true;
    protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; base.OnMouseLeave(e); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; base.OnMouseDown(e); Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; base.OnMouseUp(e); Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) pressed = true; base.OnKeyDown(e); Invalidate(); }
    protected override void OnKeyUp(KeyEventArgs e) { pressed = false; base.OnKeyUp(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; base.OnLostFocus(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        bool contrast = SystemInformation.HighContrast;
        if (contrast) { base.OnPaint(e); if (statusText.Length > 0) DrawStatus(e.Graphics, SystemColors.ControlText); return; }
        var graphics = e.Graphics;
        graphics.Clear(Parent?.BackColor ?? DesktopTheme.Canvas);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        var bounds = new RectangleF(.5f, .5f, Width - 1f, Height - 1f);
        bool command = Kind == ButtonKind.Command;
        bool filled = command && Tag is CommandRole;
        Color fill = filled ? BackColor : selected ? DesktopTheme.AccentSoft : command ? DesktopTheme.Surface : Parent?.BackColor ?? DesktopTheme.Canvas;
        Color ink = filled ? Color.White : selected ? DesktopTheme.Accent : DesktopTheme.Ink;
        if (Enabled && (hovered || pressed)) fill = filled ? ControlPaint.Light(fill, pressed ? .05f : .15f) : DesktopTheme.Hover;
        if (!Enabled) { fill = DesktopTheme.Inset; ink = DesktopTheme.Muted; }
        if (Kind != ButtonKind.Tab)
        {
            using var path = Rounded(bounds, 8 * scale);
            using var brush = new SolidBrush(fill); graphics.FillPath(brush, path);
            if (command && !filled)
            { using var pen = new Pen(DesktopTheme.Border); graphics.DrawPath(pen, path); }
            if (selected && Kind == ButtonKind.Navigation)
            { using var marker = new SolidBrush(DesktopTheme.Accent); graphics.FillRectangle(marker, 0, 10 * scale, 3 * scale, Height - 20 * scale); }
        }
        else
        {
            if (hovered) { using var brush = new SolidBrush(fill); graphics.FillRectangle(brush, ClientRectangle); }
            if (selected) { using var brush = new SolidBrush(DesktopTheme.Accent); graphics.FillRectangle(brush, 12 * scale, Height - 3 * scale, Width - 24 * scale, 3 * scale); }
        }
        var textBounds = Rectangle.FromLTRB(Padding.Left, Padding.Top, Width - Padding.Right, Height - Padding.Bottom);
        if (statusText.Length > 0) textBounds.Height = Math.Max(1, textBounds.Height - (int)(17 * scale));
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine
            | (Kind == ButtonKind.Navigation ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter);
        if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
        else if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
        TextRenderer.DrawText(graphics, Text, Font, textBounds, ink, flags);
        if (statusText.Length > 0) DrawStatus(graphics, statusActive ? DesktopTheme.Accent : DesktopTheme.Muted);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -5, -5), ink, fill);
    }
    private void DrawStatus(Graphics graphics, Color ink)
    {
        float scale = DeviceDpi / 96f;
        using var statusFont = new Font(Font.FontFamily, 8f);
        int inset = (int)(6 * scale);
        var bounds = new Rectangle(inset, Height - Padding.Bottom - (int)(17 * scale),
            Math.Max(1, Width - inset * 2), (int)(17 * scale));
        TextRenderer.DrawText(graphics, (statusActive ? "● " : "") + statusText, statusFont, bounds, ink,
            TextFormatFlags.Right | TextFormatFlags.Bottom | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
    private static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }
}

internal sealed class SectionDivider : Control
{
    internal SectionDivider() { Dock = DockStyle.Top; Height = 1; TabStop = false; SetStyle(ControlStyles.UserPaint, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : DesktopTheme.Border);
        e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }
}
internal sealed class ResponsiveStack : TableLayoutPanel
{
    private Control? observedParent;
    internal ResponsiveStack()
    {
        Dock = DockStyle.Top; AutoSize = true; ColumnCount = 1; Padding = new Padding(16); Font = DesktopTheme.BodyFont;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    }
    protected override void OnParentChanged(EventArgs e)
    {
        if (observedParent is not null) observedParent.SizeChanged -= ParentSizeChanged;
        observedParent = Parent;
        if (observedParent is not null) observedParent.SizeChanged += ParentSizeChanged;
        base.OnParentChanged(e); ConstrainWidth();
    }
    private void ParentSizeChanged(object? sender, EventArgs e) { ConstrainWidth(); PerformLayout(); }
    private void ConstrainWidth()
    {
        if (Parent is null || Parent.ClientSize.Width <= 0) return;
        int width = Math.Max(1, Parent.ClientSize.Width - Parent.Padding.Horizontal);
        if (Parent is ScrollableControl scroll && scroll.VerticalScroll.Visible) width -= SystemInformation.VerticalScrollBarWidth;
        width = Math.Max(1, width);
        if (MaximumSize.Width != width) MaximumSize = new Size(width, 0);
        foreach (Control control in Controls)
            if (control is Label label)
            {
                var maximum = new Size(Math.Max(1, width - Padding.Horizontal - label.Margin.Horizontal), 0);
                if (label.MaximumSize != maximum) label.MaximumSize = maximum;
            }
    }
    protected override void OnLayout(LayoutEventArgs e) { ConstrainWidth(); base.OnLayout(e); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && observedParent is not null) observedParent.SizeChanged -= ParentSizeChanged;
        base.Dispose(disposing);
    }
}
internal sealed class DetailsBox : TextBox
{
    [System.Diagnostics.CodeAnalysis.AllowNull]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Visible)]
    public override string Text { get => base.Text; set => base.Text = (value ?? "").ReplaceLineEndings(Environment.NewLine); }
}

internal sealed class PathPicker : UserControl
{
    private readonly TextBox input = new() { Dock = DockStyle.Fill };
    private readonly Label validation = DesktopTheme.Text("");
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string Value { get => input.Text.Trim(); set => input.Text = value; }
    internal event EventHandler? ValueChanged;
    internal PathPicker(string role, bool file = false, IEnumerable<string>? recent = null)
    {
        AutoSize = true; Dock = DockStyle.Top; AllowDrop = true;
        var layout = DesktopTheme.Stack(); layout.Padding = new Padding(0);
        DesktopTheme.Add(layout, DesktopTheme.Text(role)); input.AccessibleName = role;
        var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 4 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browse = DesktopTheme.Button("&Browse…", () =>
        {
            if (file)
            {
                using var picker = new OpenFileDialog { Title = role, CheckFileExists = true, Filter = VideoFiles.PickerFilter };
                if (picker.ShowDialog(FindForm()) == DialogResult.OK) Value = picker.FileName;
            }
            else
            {
                using var picker = new FolderBrowserDialog { Description = role, SelectedPath = Value, UseDescriptionForTitle = true };
                if (picker.ShowDialog(FindForm()) == DialogResult.OK) Value = picker.SelectedPath;
            }
        });
        row.Controls.Add(input, 0, 0); row.Controls.Add(browse, 1, 0);
        row.Controls.Add(DesktopTheme.Button("Open", () => Try(() => DesktopTheme.Open(file ? Path.GetDirectoryName(Value)! : Value))), 2, 0);
        row.Controls.Add(DesktopTheme.Button("Copy path", () => Try(() => { if (Value.Length > 0) Clipboard.SetText(Value); })), 3, 0);
        DesktopTheme.Add(layout, row); DesktopTheme.Add(layout, validation); Controls.Add(layout);
        var menu = new ContextMenuStrip();
        foreach (string path in (recent ?? []).Take(12)) menu.Items.Add(path, null, (_, _) => Value = path);
        if (menu.Items.Count > 0) input.ContextMenuStrip = menu; else menu.Dispose();
        input.TextChanged += (_, _) =>
        {
            validation.Text = Value.Length == 0 ? "Choose a path or paste one here."
                : file && !VideoFiles.IsMp4(Value) ? "Choose an .mp4 clip." : "Availability is checked when you analyze or start.";
            ValueChanged?.Invoke(this, EventArgs.Empty);
        };
        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length != 1)
            { validation.Text = "Drop exactly one " + (file ? "file." : "folder."); return; }
            if (file && !VideoFiles.IsMp4(paths[0])) { validation.Text = "Choose an .mp4 clip."; return; }
            if (file ? File.Exists(paths[0]) : Directory.Exists(paths[0])) Value = paths[0];
            else validation.Text = "This picker requires a " + (file ? "file." : "folder.");
        };
    }
    private void Try(Action action) { try { action(); } catch (Exception ex) { validation.Text = ex.Message; } }
}

internal sealed class SourceFolderList : UserControl
{
    private readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false, Height = 112, HorizontalScrollbar = true, AccessibleName = "Source folders" };
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal List<string> Paths { get => list.Items.Cast<string>().ToList(); set { list.Items.Clear(); list.Items.AddRange(value.Cast<object>().ToArray()); } }
    internal SourceFolderList(string role = "Source folders — only files directly inside each folder; add subfolders separately. Links are excluded.")
    {
        AutoSize = true; var stack = DesktopTheme.Stack(); stack.Padding = new Padding(0);
        DesktopTheme.Add(stack, DesktopTheme.Text(role));
        DesktopTheme.Add(stack, list);
        DesktopTheme.Add(stack, DesktopTheme.Actions(DesktopTheme.Button("Add folder…", () =>
        {
            using var picker = new FolderBrowserDialog();
            if (picker.ShowDialog(FindForm()) == DialogResult.OK && !Paths.Contains(picker.SelectedPath, StringComparer.OrdinalIgnoreCase)) list.Items.Add(picker.SelectedPath);
        }), DesktopTheme.Button("Remove", () => { if (list.SelectedIndex >= 0) list.Items.RemoveAt(list.SelectedIndex); })));
        var pasted = new TextBox { Width = 360, PlaceholderText = "Paste a full folder path", AccessibleName = "Folder path to add" };
        var issue = DesktopTheme.Text("");
        DesktopTheme.Add(stack, DesktopTheme.Actions(pasted, DesktopTheme.Button("Add path", () =>
        {
            try
            {
                string path = pasted.Text.Trim();
                if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Enter a full folder path.");
                path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (!Paths.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Items.Add(path);
                pasted.Clear(); issue.Text = "Added. Unavailable drives remain saved; availability is checked before use.";
            }
            catch (Exception ex) { issue.Text = ex.Message; }
        })));
        DesktopTheme.Add(stack, issue);
        Controls.Add(stack);
    }
}

internal sealed record ContextRow(string Key, string Group, string Action, string Path, string Proposed,
    string Context, string Duration, string Size, bool Selectable = false, long Bytes = 0,
    DateTimeOffset? Modified = null, string Identity = "", DateTimeOffset? Created = null, bool SelectedByDefault = false);

// Materialize only visible grid cells. Selection belongs to stable keys, never row numbers.
internal sealed class FileContextList : UserControl
{
    internal DataGridView Table { get; } = new() { Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None };
    private readonly TextBox filter = new() { Dock = DockStyle.Top, PlaceholderText = "Filter filenames, folder context or paths", AccessibleName = "Filter results" };
    private readonly DetailsBox details = new() { Dock = DockStyle.Bottom, Height = 80, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "Selected file details" };
    private readonly Label summary = DesktopTheme.Text("No results yet.");
    private IReadOnlyList<ContextRow> rows = [];
    private List<ContextRow> visible = [];
    private int sortColumn = -1;
    private bool descending;
    private readonly bool lookup;
    private readonly bool contextOnly;
    internal HashSet<string> SelectedKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal event Action? SelectionChanged;
    internal IReadOnlyList<ContextRow> Rows => rows;
    internal FileContextList(bool lookup = false, bool selection = false, bool contextOnly = false)
    {
        this.lookup = lookup || selection;
        this.contextOnly = contextOnly;
        Dock = DockStyle.Top; Height = 390; MinimumSize = new Size(0, 250);
        Table.AccessibleName = "File context review";
        foreach (var (name, width) in new[] { ("Group", 65), ("Action", 95), ("Filename", 180), ("Folder / batch", 230), ("Proposed action / path", 280), ("Duration", 100), ("Size", 100), ("Modified", 145) })
            Table.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, Width = width, SortMode = DataGridViewColumnSortMode.Programmatic });
        if (selection)
        {
            Table.Columns[1].HeaderText = "Select";
            Table.AccessibleName = "Identified clip results";
        }
        if (lookup)
        {
            Table.Columns.Add(new DataGridViewTextBoxColumn { Name = "Created", HeaderText = "Created (local)", Width = 175, SortMode = DataGridViewColumnSortMode.Programmatic });
            Table.Columns[8].DisplayIndex = 3;
            foreach (int column in new[] { 0, 5, 7 }) Table.Columns[column].Visible = false;
            Table.Columns[1].HeaderText = "Select";
            Table.Columns[1].Width = 85;
            Table.Columns[4].HeaderText = "Date source";
            Table.Columns[3].HeaderText = "Folder";
            Table.AccessibleName = "Clip creation date results";
        }
        Table.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= visible.Count) return;
            var row = visible[e.RowIndex];
            e.Value = e.ColumnIndex switch { 0 => row.Group, 1 => row.Selectable ? this.lookup ? SelectedKeys.Contains(row.Key) ? "Selected" : "—" : SelectedKeys.Contains(row.Key) ? "Recycle" : "Keep" : row.Action,
                2 => Path.GetFileName(row.Path), 3 => row.Context, 4 => row.Proposed, 5 => row.Duration, 6 => row.Size,
                8 => row.Created?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown", _ => row.Modified?.ToLocalTime().ToString("g") ?? "" };
        };
        void Toggle(int index)
        {
            if (index < 0 || index >= visible.Count || !visible[index].Selectable) return;
            string key = visible[index].Key;
            if (!SelectedKeys.Add(key)) SelectedKeys.Remove(key);
            Table.InvalidateRow(index); UpdateSummary(); SelectionChanged?.Invoke();
        }
        Table.CellDoubleClick += (_, e) => { if (!this.lookup && e.ColumnIndex == 1) Toggle(e.RowIndex); };
        Table.CellClick += (_, e) => { if (this.lookup && e.ColumnIndex == 1) Toggle(e.RowIndex); };
        Table.KeyDown += (_, e) => { if (e.KeyCode == Keys.Space) { Toggle(Table.CurrentCell?.RowIndex ?? -1); e.Handled = true; } };
        Table.SelectionChanged += (_, _) => UpdateDetails();
        Table.ColumnHeaderMouseClick += (_, e) =>
        {
            descending = sortColumn == e.ColumnIndex && !descending; sortColumn = e.ColumnIndex;
            foreach (DataGridViewColumn column in Table.Columns) column.HeaderCell.SortGlyphDirection = column.Index == sortColumn ? descending ? SortOrder.Descending : SortOrder.Ascending : SortOrder.None;
            Refilter();
        };
        filter.TextChanged += (_, _) => Refilter();
        var toggle = DesktopTheme.Button(this.lookup ? "Select / unselect row" : "Keep / Recycle selected row", () => Toggle(Table.CurrentCell?.RowIndex ?? -1));
        toggle.Visible = !contextOnly;
        var actions = DesktopTheme.Actions(toggle,
            DesktopTheme.Button("Open clip", () => External(row => DesktopTheme.Open(row.Path))),
            DesktopTheme.Button("Open containing folder", () => External(row => DesktopTheme.Open(Path.GetDirectoryName(row.Path)!))),
            DesktopTheme.Button("Copy path", () => External(row => Clipboard.SetText(row.Path))));
        actions.Dock = DockStyle.Bottom; summary.Dock = DockStyle.Bottom;
        Controls.Add(Table); Controls.Add(filter); Controls.Add(details); Controls.Add(actions); Controls.Add(summary);
    }
    internal void SetRows(IReadOnlyList<ContextRow> value, bool reset = true)
    {
        if (reset)
        {
            var prior = rows.Where(r => r.Selectable).ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
            var selected = new HashSet<string>(SelectedKeys, StringComparer.OrdinalIgnoreCase);
            SelectedKeys.Clear();
            foreach (var row in value.Where(r => r.Selectable))
            {
                bool unchanged = prior.TryGetValue(row.Key, out var old)
                    && old.Identity == row.Identity && old.Modified == row.Modified && old.Bytes == row.Bytes
                    && old.SelectedByDefault == row.SelectedByDefault;
                if (unchanged ? selected.Contains(row.Key) : row.SelectedByDefault) SelectedKeys.Add(row.Key);
            }
        }
        rows = value;
        Refilter();
    }
    internal void SetFilter(string value) => filter.Text = value;
    internal void SelectVisible()
    {
        foreach (var row in visible.Where(r => r.Selectable)) SelectedKeys.Add(row.Key);
        Table.Invalidate(); UpdateSummary(); SelectionChanged?.Invoke();
    }
    internal void ClearSelection()
    { SelectedKeys.Clear(); Table.Invalidate(); UpdateSummary(); SelectionChanged?.Invoke(); }
    private void Refilter()
    {
        visible = rows.Where(r => (r.Path + r.Context + r.Group).Contains(filter.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sortColumn >= 0)
        {
            IComparable SortValue(ContextRow r) => sortColumn switch { 0 => r.Group, 1 => r.Selectable ? SelectedKeys.Contains(r.Key) ? "Recycle" : "Keep" : r.Action,
                2 => System.IO.Path.GetFileName(r.Path), 3 => r.Context, 4 => r.Proposed, 5 => r.Duration, 6 => r.Bytes,
                8 => r.Created ?? DateTimeOffset.MaxValue, _ => r.Modified ?? DateTimeOffset.MinValue };
            visible = (descending ? visible.OrderByDescending(SortValue) : visible.OrderBy(SortValue)).ToList();
        }
        Table.RowCount = visible.Count; Table.Invalidate(); UpdateSummary(); UpdateDetails();
    }
    private void UpdateSummary() => summary.Text = contextOnly ? $"{rows.Count:N0} clips • {visible.Count:N0} visible. All reviewed clips are included in the batch."
        : lookup ? $"{rows.Count:N0} results • {visible.Count:N0} visible • {SelectedKeys.Count:N0} selected (including hidden rows). Click Select or press Space to toggle." : $"{rows.Count:N0} results • {visible.Count:N0} visible • {SelectedKeys.Count:N0} selected for recycling (including hidden rows). Space toggles eligible rows.";
    private void UpdateDetails()
    {
        int index = Table.CurrentCell?.RowIndex ?? -1;
        details.Text = index >= 0 && index < visible.Count ? visible[index].Path + Environment.NewLine + visible[index].Proposed +
            Environment.NewLine + visible[index].Identity + (visible[index].Created is { } created ? "\nEmbedded creation time (UTC): " + created.ToString("O") : "")
            + (visible[index].Modified is { } modified ? "\nExact modified time: " + modified.ToString("O") : "") : "Select a row to inspect its exact path.";
    }
    private void External(Action<ContextRow> action)
    {
        int index = Table.CurrentCell?.RowIndex ?? -1;
        if (index < 0 || index >= visible.Count) return;
        try { action(visible[index]); } catch (Exception ex) { details.Text = ex.Message; }
    }
}
