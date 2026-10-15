namespace VideoTrayApp;

internal sealed class ToolGroupWorkspace : UserControl
{
    private readonly Panel body = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel choices = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(20, 4, 16, 14) };
    private readonly Dictionary<string, UserControl> workspaces = [];
    private readonly Dictionary<string, Func<UserControl>> factories = [];
    private readonly Dictionary<string, Button> buttons = [];
    internal ToolGroupWorkspace()
    {
        Font = DesktopTheme.BodyFont; Dock = DockStyle.Fill;
        var toolHeader = DesktopTheme.Stack(); toolHeader.Padding = new Padding(0);
        DesktopTheme.Add(toolHeader, choices);
        DesktopTheme.Add(toolHeader, new SectionDivider { Margin = new Padding(0) });
        Controls.Add(body); Controls.Add(toolHeader);
    }
    internal void Add(PresetAction action, Func<WorkflowWorkspace> create)
        => Add(action.ToString(), ActionPreset.Label(action), create);
    internal void Add(string key, string label, Func<UserControl> create)
    {
        factories[key] = create;
        var choice = DesktopTheme.Button(label, () => Select(key));
        choice.Kind = ButtonKind.Tool;
        choice.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && !workspaces.ContainsKey(key))
            { Select(key); choice.ContextMenuStrip?.Show(choice, e.Location); }
        };
        choices.Controls.Add(choice); buttons[key] = choice;
        if (factories.Count == 1) Select(key);
    }
    internal void Select(PresetAction action) => Select(action.ToString());
    internal void Select(string action)
    {
        if (!workspaces.ContainsKey(action) && factories.TryGetValue(action, out var create))
        {
            var workspace = create(); workspace.Dock = DockStyle.Fill; workspace.Visible = false; body.Controls.Add(workspace);
            workspaces[action] = workspace; buttons[action].ContextMenuStrip = workspace.ContextMenuStrip;
            if (IsHandleCreated) DesktopTheme.Apply(workspace);
        }
        foreach (var pair in workspaces) pair.Value.Visible = pair.Key == action;
        foreach (var pair in buttons) ((DesktopButton)pair.Value).Selected = pair.Key == action;
        if (workspaces.TryGetValue(action, out var selected)) { selected.BringToFront(); selected.PerformLayout(); }
    }
}
