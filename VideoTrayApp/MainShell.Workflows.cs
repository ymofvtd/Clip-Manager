namespace VideoTrayApp;

internal sealed partial class MainShell
{
    private static string DestinationFor(PresetAction action) => action switch
    {
        PresetAction.PrepareBatch or PresetAction.ShuffleBatches => "Batches",
        PresetAction.IdentifyClip or PresetAction.RemoveDuplicates or PresetAction.SeekDuplications => "Find & clean",
        PresetAction.Archive => "Archive", _ => "Rename & shuffle"
    };
    private void BuildTools(string name)
    {
        var tabs = new ToolGroupWorkspace { AccessibleName = name + " tools" };
        foreach (var action in Enum.GetValues<PresetAction>().Where(a => DestinationFor(a) == name))
        {
            var selectedAction = action;
            tabs.Add(action, () =>
            {
                var workspace = new WorkflowWorkspace(this, selectedAction, jobs, () => workingFolder, preferences);
                workflows[selectedAction] = workspace; return workspace;
            });
        }
        pages[name] = tabs;
        if (name == "Find & clean")
            tabs.Add("FindByDate", "Find by date", () => new ClipDateWorkspace(this, () => workingFolder, preferences));
    }
    internal void OpenTool(PresetAction action)
    {
        string name = DestinationFor(action); Navigate(name);
        if (pages[name] is ToolGroupWorkspace tabs) tabs.Select(action);
    }
    internal async Task<JobOutcome?> RunJobAsync(string title, string context, bool analyze,
        Func<IOperationProgress, CancellationToken, JobOutcome> work)
    {
        if (shuttingDown || jobs.Busy) return null;
        var task = jobs.RunAsync(title, context, analyze, (progress, ct) =>
        {
            tracker.Pause();
            try { return work(progress, ct); }
            finally { tracker.Resume(); archive.RequestReconciliation(); }
        });
        RefreshActivity(); RefreshPresentation();
        var outcome = await task;
        await UiThread.Switch(this);
        if (!IsDisposed && !shuttingDown)
        {
            RefreshActivity(); RefreshPresentation();
            if (preferences.Notifications && !analyze && outcome is not null)
                tray.ShowBalloonTip(3000, title, DesktopTheme.StateLabel(outcome.State), outcome.State == JobState.Failed ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        return outcome;
    }
}
