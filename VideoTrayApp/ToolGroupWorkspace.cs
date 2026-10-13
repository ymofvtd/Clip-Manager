namespace VideoTrayApp;

internal sealed class ToolGroupWorkspace : UserControl
{
    private readonly Panel body = new() { Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel choices = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
    private readonly Dictionary<PresetAction, WorkflowWorkspace> workspaces = [];
    private readonly Dictionary<PresetAction, Func<WorkflowWorkspace>> factories = [];
    private readonly Dictionary<PresetAction, Button> buttons = [];
    internal ToolGroupWorkspace() { Font = DesktopTheme.BodyFont; Dock = DockStyle.Fill; Controls.Add(body); Controls.Add(choices); }
    internal void Add(PresetAction action, Func<WorkflowWorkspace> create)
    {
        factories[action] = create;
        var choice = DesktopTheme.Button(ActionPreset.Label(action), () => Select(action));
        choice.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && !workspaces.ContainsKey(action))
            { Select(action); choice.ContextMenuStrip?.Show(choice, e.Location); }
        };
        choices.Controls.Add(choice); buttons[action] = choice;
        if (factories.Count == 1) Select(action);
    }
    internal void Select(PresetAction action)
    {
        if (!workspaces.ContainsKey(action) && factories.TryGetValue(action, out var create))
        {
            var workspace = create(); workspace.Dock = DockStyle.Fill; workspace.Visible = false; body.Controls.Add(workspace);
            workspaces[action] = workspace; buttons[action].ContextMenuStrip = workspace.ContextMenuStrip;
            if (IsHandleCreated) DesktopTheme.Apply(workspace);
        }
        foreach (var pair in workspaces) pair.Value.Visible = pair.Key == action;
        if (workspaces.TryGetValue(action, out var selected)) { selected.BringToFront(); selected.PerformLayout(); }
    }
}
