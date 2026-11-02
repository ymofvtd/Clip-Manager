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
        {
            tabs.Add("SeekClips", "Seek clips", () => new ClipSeekWorkspace(this, () => workingFolder, preferences));
        }
        if (name == "Batches")
        {
            tabs.Add("BestOfMonth", "Best of the month", () => monthlyPage = new MonthlyBatchWorkspace(this, preferences, monthlySettings, workingFolder));
            tabs.Add("PreparingCompilation", "Preparing compilation", () => new CompilationWorkspace(this, preferences));
        }
    }
    internal void OpenTool(PresetAction action)
    {
        string name = DestinationFor(action); Navigate(name);
        if (pages[name] is ToolGroupWorkspace tabs) tabs.Select(action);
    }
    internal Task<JobOutcome?> AddToCompilationAsync(IEnumerable<string> paths)
    {
        if (Compilations.Active is not { } session)
            return Task.FromResult<JobOutcome?>(new(JobState.Failed, "First open Batches → Preparing compilation, choose a folder, and Lock in folder. Then add your selected clips."));
        string[] selected = paths.ToArray();
        return RunJobAsync("Add to compilation", $"Session: {session.Name}\nFolder: {session.Folder}\nSelected clips:\n" + string.Join("\n", selected), false,
            (progress, ct) => JobOutcome.Complete(CompilationPreparer.Add(session, selected, progress, ct)));
    }
    internal async Task<JobOutcome?> RunJobAsync(string title, string context, bool analyze,
        Func<IOperationProgress, CancellationToken, JobOutcome> work)
    {
        if (shuttingDown || jobs.Busy) return null;
        var task = jobs.RunAsync(title, context, analyze, (progress, ct) =>
        {
            tracker.Pause();
            try { return work(progress, ct); }
            finally { tracker.Resume(); archives.RequestReconciliation(); }
        });
        RefreshActivity(); RefreshPresentation();
        var outcome = await task;
        await UiThread.Switch(this);
        if (!IsDisposed && !shuttingDown)
        {
            RefreshActivity(); RefreshPresentation();
            if ((preferences.Notifications || title == "Automatic best of the month" && monthlySettings.NotifyOnCompletion) && !analyze && outcome is not null)
            {
                string message = title.Contains("best of the month", StringComparison.OrdinalIgnoreCase)
                    ? outcome.Details.Split('\n')[0] : DesktopTheme.StateLabel(outcome.State);
                tray.ShowBalloonTip(3000, title, message[..Math.Min(message.Length, 240)], outcome.State == JobState.Failed ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
        }
        return outcome;
    }
}
