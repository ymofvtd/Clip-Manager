namespace VideoTrayApp;

internal sealed partial class WorkflowWorkspace : UserControl
{
    internal PresetAction Action { get; }
    private readonly MainShell shell;
    private readonly JobController jobs;
    private readonly Func<string> workingFolder;
    private readonly PathPicker source, destination, reference, directoryB;
    private readonly CheckBox useWorking = new() { Text = "Use current working folder", AutoSize = true };
    private readonly CheckBox recursive = new() { Text = "Include subfolders", AutoSize = true, Checked = true };
    private readonly ComboBox selection = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250, AccessibleName = "Batch selection" };
    private IdentifyBehavior identifyBehavior = IdentifyBehavior.Move;
    private readonly NumericUpDown minutes = new() { Minimum = 1, Maximum = 10080, Value = 30, Width = 100, AccessibleName = "Target minutes" };
    private readonly NumericUpDown startNumber = new() { Minimum = 0, Maximum = int.MaxValue, Width = 100, AccessibleName = "Starting number" };
    private readonly NumericUpDown padding = new() { Minimum = 0, Maximum = 64, Width = 80, AccessibleName = "Padding width" };
    private readonly NumericUpDown randomLength = new() { Minimum = 4, Maximum = 64, Value = 12, Width = 80, AccessibleName = "Random name length" };
    private readonly TextBox prefix = new() { Width = 140, AccessibleName = "Filename prefix" };
    private readonly TextBox batchName = new() { Width = 200, AccessibleName = "New batch folder name" };
    private readonly DetailsBox result = new() { Text = "Configure the tool, then analyze. Analysis makes no file changes.", Multiline = true, ReadOnly = true, Dock = DockStyle.Bottom, Height = 88, ScrollBars = ScrollBars.Vertical, AccessibleName = "Operation result and recovery details" };
    private readonly FileContextList review;
    private readonly Button analyze, run;
    private readonly TableLayoutPanel fields;
    private readonly ComboBox presets = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Saved preset" };
    private List<ActionPreset> savedPresets = [];
    private Func<IOperationProgress, CancellationToken, JobOutcome>? execution;
    private string frozenContext = "";
    private string recovery = "";
    private bool busy;
    private int revision;
    private readonly Panel stages = new() { Dock = DockStyle.Fill };
    private readonly Panel configurationPage = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Panel reviewPage = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly DesktopButton configureTab, reviewTab;
    private Func<ISet<string>, string?>? validateSelection;

    internal WorkflowWorkspace(MainShell shell, PresetAction action, JobController jobs, Func<string> workingFolder, UiPreferences preferences)
    {
        this.shell = shell; Action = action; this.jobs = jobs; this.workingFolder = workingFolder;
        review = new(selection: action == PresetAction.IdentifyClip);
        Font = DesktopTheme.BodyFont; Dock = DockStyle.Fill; SuspendLayout();
        source = new(action == PresetAction.SeekDuplications ? "Directory A" : action == PresetAction.IdentifyClip ? "Search folder" : "Source / target folder", recent: preferences.RecentPaths);
        destination = new(action == PresetAction.PrepareBatch ? "Destination parent — highest numbered c_ folder or a new batch" : "Move destination", recent: preferences.RecentPaths);
        reference = new("Reference clip — excluded from all changes", file: true, recent: preferences.RecentPaths);
        directoryB = new("Directory B — neither directory has priority", recent: preferences.RecentPaths);
        selection.Items.AddRange(["Numbered MP4 (top level)", "All MP4 files (top level)", "Archives (MP4, recursive, excluding Backup)"]); selection.SelectedIndex = 0;
        fields = DesktopTheme.Stack();
        fields.Tag = SurfaceRole.Surface;
        fields.Padding = new Padding(24, 16, 24, 24);
        DesktopTheme.Add(fields, DesktopTheme.Text(Description(action)));
        DesktopTheme.Add(fields, DesktopTheme.Actions(DesktopTheme.Text("Preset"), presets,
            DesktopTheme.Button("Apply preset", ApplySelectedPreset), DesktopTheme.Button("Save preset…", SavePreset),
            DesktopTheme.Button("Manage presets…", () => shell.OpenSettingsSection("Presets"))));
        presets.DropDown += (_, _) => ReloadPresets();
        DesktopTheme.Add(fields, useWorking); DesktopTheme.Add(fields, source);
        if (action == PresetAction.SeekDuplications) { DesktopTheme.Add(fields, directoryB); DesktopTheme.Add(fields, recursive); }
        if (action == PresetAction.IdentifyClip) DesktopTheme.Add(fields, reference);
        if (action == PresetAction.PrepareBatch)
        {
            DesktopTheme.Add(fields, destination);
            DesktopTheme.Add(fields, DesktopTheme.Actions(DesktopTheme.Text("Target minutes"), minutes, selection));
            DesktopTheme.Add(fields, DesktopTheme.Actions(DesktopTheme.Text("New batch name (optional)"), batchName));
        }
        if (action == PresetAction.ShuffleBatches) DesktopTheme.Add(fields, DesktopTheme.Actions(DesktopTheme.Text("Maximum minutes per batch"), minutes));
        if (action is PresetAction.ShuffleAndName or PresetAction.NameByTens)
            DesktopTheme.Add(fields, DesktopTheme.Actions(DesktopTheme.Text("Starting number"), startNumber, DesktopTheme.Text("Padding"), padding, DesktopTheme.Text("Prefix"), prefix));
        if (action == PresetAction.ShuffleRandom) DesktopTheme.Add(fields, DesktopTheme.Actions(DesktopTheme.Text("Random name length"), randomLength));
        analyze = DesktopTheme.Button("Analyze / refresh preview", async () => await AnalyzeAsync());
        analyze.Tag = CommandRole.Primary;
        run = DesktopTheme.Button("Review and run…", async () => await RunAsync()); run.Enabled = false;
        run.Visible = action != PresetAction.IdentifyClip;
        configurationPage.Controls.Add(fields);
        review.Dock = DockStyle.Fill; reviewPage.Controls.Add(review);
        var identifyCommands = action == PresetAction.IdentifyClip ? BuildIdentifyCommands() : null;
        if (identifyCommands is not null) reviewPage.Controls.Add(identifyCommands);
        reviewPage.SizeChanged += (_, _) =>
        {
            review.Dock = reviewPage.ClientSize.Height - (identifyCommands?.Height ?? 0) < 320 ? DockStyle.Top : DockStyle.Fill;
            if (review.Dock == DockStyle.Top) review.Height = 390;
        };
        stages.Controls.Add(configurationPage); stages.Controls.Add(reviewPage); reviewPage.Visible = false; Controls.Add(stages);
        configureTab = DesktopTheme.Button("Configure", ShowConfiguration);
        reviewTab = DesktopTheme.Button("Review files", ShowReview);
        configureTab.Kind = reviewTab.Kind = ButtonKind.Tab;
        configureTab.Selected = true;
        configureTab.Margin = reviewTab.Margin = new Padding(0, 0, 8, 0);
        var stageButtons = DesktopTheme.Actions(configureTab, reviewTab);
        stageButtons.Padding = new Padding(20, 12, 0, 0); stageButtons.Tag = SurfaceRole.Surface;
        stageButtons.AccessibleName = "Workspace views";
        Controls.Add(stageButtons);
        result.Tag = SurfaceRole.Inset;
        result.BorderStyle = BorderStyle.None;
        var commands = DesktopTheme.Actions(analyze, run); commands.Dock = DockStyle.Bottom; commands.Padding = new Padding(20, 10, 16, 10); commands.Tag = SurfaceRole.Surface;
        var resultPanel = new Panel { Dock = DockStyle.Bottom, Height = 88, Padding = new Padding(24, 10, 24, 10), Tag = SurfaceRole.Inset };
        result.Dock = DockStyle.Fill; resultPanel.Controls.Add(result);
        Controls.Add(resultPanel); Controls.Add(commands);
        source.Value = workingFolder();
        VisibleChanged += (_, _) => { if (Visible && useWorking.Checked) source.Value = workingFolder(); };
        useWorking.CheckedChanged += (_, _) => { source.Enabled = !useWorking.Checked; if (useWorking.Checked) source.Value = workingFolder(); InvalidatePlan(); };
        foreach (var picker in new[] { source, destination, reference, directoryB }) picker.ValueChanged += (_, _) => InvalidatePlan();
        foreach (var input in new[] { minutes, startNumber, padding, randomLength }) input.ValueChanged += (_, _) => InvalidatePlan();
        foreach (var input in new[] { prefix, batchName }) input.TextChanged += (_, _) => InvalidatePlan();
        selection.SelectedIndexChanged += (_, _) => InvalidatePlan(); recursive.CheckedChanged += (_, _) => InvalidatePlan();
        review.SelectionChanged += () => { run.Enabled = !busy && execution is not null; RefreshIdentifyActions(); };
        ReloadPresets();
        try
        {
            var preset = PresetStore.Load(Path.Combine(shell.DataFolder, "presets.json")).DefaultFor(action);
            if (preset is not null) ApplyPreset(preset);
        }
        catch (Exception ex) { result.Text = "Presets need attention: " + ex.Message; }
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            try
            {
                foreach (var preset in PresetStore.Load(Path.Combine(shell.DataFolder, "presets.json")).Presets.Where(p => p.Action == Action))
                {
                    var captured = preset;
                    menu.Items.Add(preset.Name, null, (_, _) => ApplyPreset(captured));
                }
                if (menu.Items.Count == 0) menu.Items.Add(new ToolStripMenuItem("No saved presets for this action") { Enabled = false });
            }
            catch (Exception ex) { result.Text = "Presets could not be loaded: " + ex.Message; }
            menu.Items.Add("Manage presets…", null, (_, _) => shell.OpenSettingsSection("Presets"));
            DesktopTheme.StyleMenu(menu);
        };
        ContextMenuStrip = menu;
        if (preferences.TableWidths.TryGetValue(Action.ToString(), out var widths) && widths is not null)
            for (int i = 0; i < Math.Min(widths.Length, review.Table.ColumnCount); i++) review.Table.Columns[i].Width = Math.Clamp(widths[i], 45, 1200);
        review.Table.ColumnWidthChanged += (_, _) => preferences.TableWidths[Action.ToString()] = review.Table.Columns.Cast<DataGridViewColumn>().Select(c => c.Width).ToArray();
        ResumeLayout(false);
    }
    private void InvalidatePlan()
    { revision++; execution = null; identifyScan = null; RefreshIdentifyActions(); run.Enabled = false; result.Text = "Configuration changed. Analyze again to review these settings; no files have changed."; }
    private ActionPreset Draft() => new()
    {
        Action = Action, UseWorkingFolder = false, Target = useWorking.Checked ? workingFolder() : source.Value,
        Destination = destination.Value, ReferenceClip = reference.Value, DirectoryB = directoryB.Value,
        IncludeSubfolders = recursive.Checked, DurationMinutes = (int)minutes.Value, Selection = (BatchSelection)selection.SelectedIndex,
        Start = (int)startNumber.Value, Padding = (int)padding.Value, Prefix = prefix.Text, RandomLength = (int)randomLength.Value,
        BatchFolderName = batchName.Text, IdentifyBehavior = identifyBehavior
    };
    internal void ApplyPreset(ActionPreset preset)
    {
        useWorking.Checked = preset.UseWorkingFolder; source.Value = preset.ResolveTarget(workingFolder());
        destination.Value = preset.Destination; reference.Value = preset.ReferenceClip; directoryB.Value = preset.DirectoryB;
        recursive.Checked = preset.IncludeSubfolders; minutes.Value = Math.Clamp(preset.DurationMinutes, 1, 10080);
        selection.SelectedIndex = Math.Clamp((int)preset.Selection, 0, 2); startNumber.Value = Math.Max(0, preset.Start);
        padding.Value = Math.Clamp(preset.Padding, 0, 64); prefix.Text = preset.Prefix; randomLength.Value = Math.Clamp(preset.RandomLength, 4, 64);
        batchName.Text = preset.BatchFolderName; identifyBehavior = preset.IdentifyBehavior;
        InvalidatePlan(); result.Text = $"Preset “{preset.Name}” applied. Review the resolved folders and analyze before running.";
    }
    private void ReloadPresets()
    {
        try
        {
            savedPresets = PresetStore.Load(Path.Combine(shell.DataFolder, "presets.json")).Presets.Where(p => p.Action == Action).ToList();
            presets.Items.Clear(); presets.Items.Add("Current configuration");
            presets.Items.AddRange(savedPresets.Select(p => (object)p.Name).ToArray()); presets.SelectedIndex = 0;
        }
        catch (Exception ex) { result.Text = "Could not load presets: " + ex.Message; }
    }
    private void ApplySelectedPreset() { if (presets.SelectedIndex > 0) ApplyPreset(savedPresets[presets.SelectedIndex - 1]); }
    private void SavePreset()
    {
        try
        {
            var draft = Draft(); draft.UseWorkingFolder = useWorking.Checked;
            using var editor = new PresetEditorForm(draft, workingFolder()); DesktopTheme.Apply(editor);
            if (editor.ShowDialog(shell) != DialogResult.OK) return;
            var store = PresetStore.Load(Path.Combine(shell.DataFolder, "presets.json")); store.Presets.Add(editor.Preset); store.Save(Path.Combine(shell.DataFolder, "presets.json"));
            ReloadPresets(); result.Text = "Preset saved.";
        }
        catch (Exception ex) { result.Text = "Preset could not be saved: " + ex.Message; }
    }
    internal void SetBusy(bool value)
    { busy = value; analyze.Enabled = !value && !identifyActionBusy; run.Enabled = !value && execution is not null; RefreshIdentifyActions(); }

    internal async Task AnalyzeAsync()
    {
        if (jobs.Busy || identifyActionBusy) return;
        int analyzedRevision = revision;
        var draft = Draft();
        frozenContext = Action switch
        {
            PresetAction.SeekDuplications => $"Directory A: {draft.Target}\nDirectory B: {draft.DirectoryB}\nInclude subfolders: {draft.IncludeSubfolders}",
            PresetAction.IdentifyClip => $"Search folder: {draft.Target}\nReference: {draft.ReferenceClip}",
            PresetAction.PrepareBatch => $"Source: {draft.Target}\nDestination parent: {draft.Destination}",
            _ => "Target folder: " + draft.Target
        };
        execution = null; run.Enabled = false;
        identifyScan = null; RefreshIdentifyActions();
        IdentifyClipScan? nextIdentifyScan = null;
        validateSelection = null;
        IReadOnlyList<ContextRow> rows = [];
        Func<IOperationProgress, CancellationToken, JobOutcome>? nextExecution = null;
        string summary = "";
        bool hasIssues = false;
        var outcome = await shell.RunJobAsync(ActionPreset.Label(Action) + " — analyze", frozenContext, true, (progress, ct) =>
        {
            draft.Target = Path.GetFullPath(draft.Target);
            if (Action != PresetAction.IdentifyClip && draft.Destination.Length > 0) draft.Destination = Path.GetFullPath(draft.Destination);
            if (draft.DirectoryB.Length > 0) draft.DirectoryB = Path.GetFullPath(draft.DirectoryB);
            if (draft.ReferenceClip.Length > 0) draft.ReferenceClip = Path.GetFullPath(draft.ReferenceClip);
            ActionPreset.ValidateFolder(draft.Target);
            switch (Action)
            {
                case PresetAction.ShuffleAndName:
                case PresetAction.NameByTens:
                case PresetAction.ShuffleRandom:
                    var rename = RenamePlan.Analyze(draft.Target, Action, draft.Start, draft.Padding, draft.Prefix, draft.RandomLength, MainShell.VideoExtensions, progress, ct);
                    rows = rename.Changes.Select(c => new ContextRow(c.File.Path, "", "Rename", c.File.Path,
                        c.Destination, Path.GetDirectoryName(c.File.Path)!, "", FormatSize(c.File.Length), Bytes: c.File.Length,
                        Modified: c.File.LastWrite, Identity: $"File identity: {c.File.Physical.Volume:X8}:{c.File.Physical.Index:X16}; reviewed size and timestamps")).ToArray();
                    nextExecution = (p, token) => JobOutcome.Complete(rename.Execute(p, token));
                    summary = rename.Changes.Count == 0 ? "No top-level MP4 files found. Nothing to rename." : $"{rename.Changes.Count} files reviewed. Run uses this exact mapping; refresh generates a new random mapping.";
                    recovery = "Cancellation or failure attempts in-process rollback. Forced termination is outside this guarantee.";
                    break;
                case PresetAction.ShuffleBatches:
                    var shuffle = ClipBatchShuffler.Analyze(draft.Target, TimeSpan.FromMinutes(draft.DurationMinutes), MainShell.VideoExtensions, progress, ct);
                    rows = shuffle.Changes.Select(c => new ContextRow(c.Clip.Path, Path.GetFileName(Path.GetDirectoryName(c.Destination))!, "Move / rename", c.Clip.Path, c.Destination,
                        Path.GetRelativePath(draft.Target, Path.GetDirectoryName(c.Clip.Path)!), Duration(c.Clip.Duration), FormatSize(c.Clip.Length))).ToArray();
                    nextExecution = (p, token) => JobOutcome.Complete(ClipBatchShuffler.Execute(shuffle, p, token));
                    summary = $"{shuffle.Changes.Count} clips → {shuffle.BatchCount} batches. {shuffle.Changes.Count(c => c.Clip.Duration > shuffle.Limit)} over-limit clips get their own batch. No backups or recycling.";
                    recovery = "Cancellation or failure restores moves and new batch folders where possible. Recovery failures retain affected files.";
                    break;
                case PresetAction.PrepareBatch:
                    var prepare = PrepareBatchPlan.Analyze(draft, draft.Target, MainShell.VideoExtensions, progress, ct);
                    rows = prepare.Changes.Select(c => ChangeRow(c, "Backup / move"))
                        .Concat(prepare.StarterPaths.Where(path => !prepare.DuplicateGroups.Any(g => g.Duplicates.Contains(path, StringComparer.OrdinalIgnoreCase)))
                            .Select(path => new ContextRow(path, "Starter", "Keep", path, "Starter filename preserved", Path.GetDirectoryName(path)!,
                                Duration(prepare.Durations[path]), FormatSize(prepare.Guards.First(g => g.Path == path).Length))))
                        .Concat(prepare.DuplicateGroups.SelectMany((g, i) => g.Duplicates.Select(path => new ContextRow(path, (i + 1).ToString(), "Recycle", path,
                            "Recycle; keeper: " + g.Keep, Path.GetDirectoryName(path)!, Duration(g.Identity.Duration), FormatSize(g.Identity.Size), Bytes: g.Identity.Size)))).ToArray();
                    nextExecution = (p, token) => JobOutcome.Complete(prepare.Execute(p, token));
                    summary = $"Batch: {prepare.Batch.Path} ({(prepare.Batch.Create ? "create" : "existing starter folder")})\nStarters: {prepare.StarterCount}, {Duration(prepare.StarterDuration)}. Planned total: {Duration(prepare.TotalDuration)}.\nMove/backup {prepare.Changes.Count}; recycle {prepare.DuplicateGroups.Sum(g => g.Duplicates.Count)}. Originals backed up under {Path.Combine(draft.Target, "Backup")}.";
                    recovery = "This reviewed workflow uses an in-process transaction for recycling, backups and moves. Cancellation/failure attempts full rollback; blocked recovery retains recovery files. Legacy manual operations retain their previous contract.";
                    break;
                case PresetAction.SeekDuplications:
                    var scan = ClipDuplicationSeeker.Find(draft.Target, draft.DirectoryB, draft.IncludeSubfolders, MainShell.VideoExtensions, progress, ct);
                    hasIssues = scan.Errors.Count > 0;
                    rows = scan.Groups.SelectMany((g, i) => g.Copies.Select(c => new ContextRow(c.Path, (i + 1).ToString(), "Keep", c.Path,
                        c.HardLink ? "Hard-link alias: Keep" : "Keep or recycle individually", c.Side + " / " + Path.GetFileName(c.Root) + " / " + Path.GetDirectoryName(c.RelativePath),
                        Duration(c.Duration), FormatSize(c.Size), !c.HardLink, c.Size, c.LastWriteTime, $"SHA-256: {g.Identity.Hash}\nPhysical identity: {c.Physical.Volume:X}:{c.Physical.Index:X}; links: {c.Physical.Links}"))).ToArray();
                    validateSelection = selected => scan.Groups.Any(g => g.Copies.All(c => selected.Contains(c.Path))) ? "Keep at least one copy in every group." : null;
                    nextExecution = (p, token) =>
                    {
                        // Selection is captured on the UI thread by RunAsync, never read from live controls during execution.
                        var selected = frozenSelection;
                        var applied = ClipDuplicationSeeker.Apply(scan, selected, p, token);
                        return new(applied.Cancelled ? JobState.Cancelled : applied.Errors.Count > 0 ? JobState.CompletedWithIssues : JobState.Completed, applied.Summary + "\nRecovery: Windows Recycle Bin. Earlier completed recycling is retained.");
                    };
                    summary = scan.Groups.Count == 0 ? scan.Errors.Count > 0 ? "Scan incomplete; unreadable paths remain. No claim of no duplicates can be made." : "Complete scan: no exact groups spanning A and B." : $"{scan.Groups.Count} exact groups. All copies start Keep. Keep at least one per group; hard-link aliases stay Keep.";
                    summary += $"\nUnreadable paths: {scan.Errors.Count}. Only discovered groups are shown.\n" + string.Join("\n", scan.Errors.Take(20));
                    recovery = "Kept and selected copies are verified again. Recycle Bin only. Cancellation retains earlier successful recycling; no automatic undo.";
                    if (scan.Groups.Count == 0) nextExecution = null;
                    break;
                case PresetAction.RemoveDuplicates:
                    var clean = ClipDuplicateCleaner.Find(draft.Target, MainShell.VideoExtensions, progress, ct);
                    hasIssues = clean.Errors.Count > 0;
                    var cleanupGuards = clean.Groups.SelectMany(g => new[] { g.Keep }.Concat(g.Duplicates)
                        .Select(path => PlannedFile.ReadExpected(path, g.Identity, ct))).ToArray();
                    var cleanupMetadata = cleanupGuards.ToDictionary(g => g.Path, StringComparer.OrdinalIgnoreCase);
                    rows = clean.Groups.SelectMany((g, i) => new[] { new ContextRow(g.Keep, (i + 1).ToString(), "Keep", g.Keep, "Alphabetical keeper", Path.GetDirectoryName(g.Keep)!, Duration(g.Identity.Duration), FormatSize(g.Identity.Size), Bytes: g.Identity.Size) }
                        .Concat(g.Duplicates.Select(path => new ContextRow(path, (i + 1).ToString(), "Recycle", path, "Recycle; keeper: " + g.Keep, Path.GetDirectoryName(path)!, Duration(g.Identity.Duration), FormatSize(g.Identity.Size), Bytes: g.Identity.Size)))).ToArray();
                    rows = rows.Select(row => row with { Modified = cleanupMetadata[row.Path].LastWrite,
                        Identity = "Reviewed SHA-256: " + cleanupMetadata[row.Path].Hash }).ToArray();
                    // A partial scan is inspectable, but this strict cleanup route blocks mutation until the scope can be completely verified.
                    if (clean.Errors.Count == 0 && clean.DuplicateCount > 0) nextExecution = (p, token) => JobOutcome.Complete(PlanExecution.Transaction(p, token,
                        () =>
                        {
                            foreach (var file in cleanupGuards) file.Verify(token);
                            return ClipDuplicateCleaner.Apply(clean, p, token, stopBatchOnError: true);
                        }));
                    summary = clean.Errors.Count > 0 ? "Incomplete scan; resolve unreadable paths before recycling.\n" + string.Join("\n", clean.Errors.Take(20)) : clean.DuplicateCount == 0 ? "Complete scan: no exact duplicate clips found." : $"{clean.DuplicateCount} extra copies. Keep first full path alphabetically per group.";
                    recovery = "This workflow stages removals transactionally and attempts rollback if recycling fails or cancellation occurs. Windows Recycle Bin supplies recovery after success.";
                    break;
                case PresetAction.IdentifyClip:
                    var matches = ClipIdentifier.Find(draft.ReferenceClip, draft.Target, MainShell.VideoExtensions, progress, ct);
                    hasIssues = matches.Errors.Count > 0;
                    var referenceGuard = PlannedFile.ReadExpected(draft.ReferenceClip, matches.Reference, ct);
                    var identified = new List<PlannedFile>();
                    foreach (var path in matches.Matches)
                    {
                        var guard = PlannedFile.ReadExpected(path, matches.Reference, ct);
                        if (guard.Physical == referenceGuard.Physical) continue;
                        identified.Add(guard);
                    }
                    rows = identified.Select(file => new ContextRow(file.Path, "1", "", file.Path,
                        "Exact match; reference: " + referenceGuard.Path, Path.GetDirectoryName(file.Path)!, Duration(matches.Reference.Duration), FormatSize(file.Length),
                        Selectable: true, Bytes: file.Length, Modified: file.LastWrite, Identity: "Reviewed SHA-256: " + file.Hash)).ToArray();
                    if (!hasIssues) nextIdentifyScan = new(referenceGuard, identified.ToArray());
                    summary = matches.Errors.Count > 0 ? "Incomplete scan. Actions are blocked until unreadable paths are resolved.\n" + string.Join("\n", matches.Errors.Take(20)) : $"Complete scan: {identified.Count} independent exact copies. Select clips or use Select all visible, then Move selected or Delete selected. The reference and its aliases are excluded.";
                    recovery = "Reviewed actions use in-process rollback on failure/cancellation. Successful recycling is recoverable through Windows Recycle Bin.";
                    break;
            }
            return new(hasIssues ? JobState.CompletedWithIssues : JobState.ReadyToReview, summary);
        });
        await UiThread.Switch(this);
        if (IsDisposed || outcome is null) return;
        review.SetRows(rows); result.Text = frozenContext + "\n" + (outcome.State is JobState.Failed or JobState.Cancelled ? outcome.Details : summary + "\n" + recovery);
        execution = outcome.State is JobState.Failed or JobState.Cancelled ? null : nextExecution;
        identifyScan = outcome.State is JobState.Failed or JobState.Cancelled ? null : nextIdentifyScan;
        if (revision != analyzedRevision) { execution = null; identifyScan = null; result.Text += "\nDraft changed during analysis. Analyze again before running."; }
        RefreshIdentifyActions();
        ShowReview();
        review.Show(); review.CreateControl(); review.BringToFront(); review.PerformLayout();
        run.Enabled = execution is not null && !jobs.Busy;
        foreach (string path in new[] { draft.Target, draft.Destination, draft.DirectoryB }) shell.RememberPath(path);
    }
    private HashSet<string> frozenSelection = new(StringComparer.OrdinalIgnoreCase);
    private void ShowConfiguration()
    {
        reviewPage.Hide(); configurationPage.Show(); configurationPage.BringToFront();
        configureTab.Selected = true; reviewTab.Selected = false;
    }
    private void ShowReview()
    {
        configurationPage.Hide(); reviewPage.Show(); reviewPage.BringToFront();
        configureTab.Selected = false; reviewTab.Selected = true;
    }
    private async Task RunAsync()
    {
        if (execution is null || jobs.Busy) return;
        frozenSelection = new(review.SelectedKeys, StringComparer.OrdinalIgnoreCase);
        string? selectionError = validateSelection?.Invoke(frozenSelection);
        if (selectionError is not null) { result.Text = selectionError; return; }
        var mutations = review.Rows.Where(r => r.Action is "Rename" or "Move / rename" or "Move" or "Recycle" or "Backup / move" || r.Selectable && frozenSelection.Contains(r.Key))
            .Select(r => r.Selectable ? r with { Action = "Recycle", Proposed = "Recycle; verified keeper: " + review.Rows.First(k => k.Group == r.Group && !frozenSelection.Contains(k.Key)).Path } : r).ToList();
        if (mutations.Count == 0) { result.Text = "No changes selected. Mark individual copies Recycle or choose a mutation and analyze."; return; }
        using var confirmation = new ReviewConfirmation(ActionPreset.Label(Action), frozenContext, mutations, recovery);
        if (confirmation.ShowDialog(shell) != DialogResult.OK) return;
        var work = execution; execution = null; run.Enabled = false;
        var outcome = await shell.RunJobAsync(ActionPreset.Label(Action), frozenContext, false, work);
        await UiThread.Switch(this);
        if (!IsDisposed && outcome is not null) result.Text = DesktopTheme.StateLabel(outcome.State) + "\n" + outcome.Details;
    }
    internal static string Duration(TimeSpan value) => $"{(long)value.TotalHours}h {value.Minutes:00}m {value.Seconds:00}s";
    internal static string FormatSize(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000d:0.00} GB" : $"{bytes / 1_000_000d:0.00} MB";
    private static ContextRow ChangeRow(PlannedChange change, string action) => new(change.File.Path, "", action, change.File.Path,
        change.Destination + (change.Backup is null ? "" : "\nBackup: " + change.Backup), Path.GetDirectoryName(change.File.Path)!,
        change.Duration is { } duration ? Duration(duration) : "", FormatSize(change.File.Length), Bytes: change.File.Length,
        Modified: change.File.LastWrite, Identity: "Reviewed SHA-256: " + change.File.Hash);
    private static string Description(PresetAction action) => action switch
    {
        PresetAction.SeekDuplications => "Compare folders (Seek duplications). Exact size, duration and full SHA-256 identity only. Overlapping paths appear once; neither A nor B has priority.",
        PresetAction.RemoveDuplicates => "Clean one folder (Remove duplicates). Includes subfolders, excludes links. Keeps the first full path alphabetically per exact-identity group. Use Compare folders for individual choices.",
        PresetAction.IdentifyClip => "Identify clip. Search recursively for exact SHA-256 content and duration matches, then select results to move to an isolation folder or delete through Windows Recycle Bin. Reference clip stays untouched.",
        PresetAction.PrepareBatch => "Prepare Batch. Randomly select, check exact duplicates and refill, back up final clips, then move them shortest to longest. Analyze resolves the c_ starter folder or exact new batch without changing files.",
        PresetAction.ShuffleBatches => "Shuffle Batches. Pool recursive archive MP4 files, excluding Backup and links, pack intact clips into duration-limited batches and number shortest to longest. No duplicate cleanup or backups.",
        PresetAction.NameByTens => "Name by 10s. Numbering advances by ten (0, 10, 20…). This is a numbering step, not a duration filter. MP4 extensions are preserved.",
        PresetAction.ShuffleAndName => "Shuffle & Name. Randomize clips, then number in steps of ten with optional padding and prefix. MP4 extensions are preserved.",
        _ => "Shuffle Random. Rename clips using a fixed reviewed random mapping, preserving MP4 extensions. Refresh preview explicitly to generate another mapping."
    };
}

internal sealed class ReviewConfirmation : Form
{
    internal ReviewConfirmation(string title, string context, IReadOnlyList<ContextRow> changes, string recovery)
    {
        Text = "Confirm — " + title; Font = new Font("Segoe UI", 10.5f); ClientSize = new Size(900, 560);
        MinimumSize = new Size(600, 400); StartPosition = FormStartPosition.CenterParent;
        var stack = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize)); stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        long bytes = changes.Where(c => c.Action == "Recycle").Sum(c => c.Bytes);
        stack.Controls.Add(DesktopTheme.Text($"Review exactly {changes.Count:N0} changes, including selections hidden by filters.\nSelected content for recycling: {bytes:N0} bytes (actual reclaimed disk space may differ).\n{context}\n{recovery}"), 0, 0);
        stack.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false,
            AccessibleName = "Exact confirmed paths and actions", Text = string.Join(Environment.NewLine + Environment.NewLine, changes.Select(c => c.Action + ": " + c.Path + Environment.NewLine + c.Proposed)) }, 0, 1);
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var execute = new Button { Text = "Run reviewed changes", AutoSize = true, DialogResult = DialogResult.OK };
        execute.Tag = changes.Any(c => c.Action == "Recycle") ? CommandRole.Recycle : CommandRole.Primary;
        stack.Controls.Add(DesktopTheme.Actions(cancel, execute), 0, 2); Controls.Add(stack);
        CancelButton = cancel; AcceptButton = cancel; ActiveControl = cancel; DesktopTheme.Apply(this);
    }
}
