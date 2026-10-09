namespace VideoTrayApp;

public partial class Form1
{
    private static string PresetConfigPath => Path.Combine(AppDataFolder, "presets.json");

    private void btnPresets_Click(object? sender, EventArgs e)
    {
        if (operationCts is not null) return;
        try
        {
            using var window = new PresetsForm(PresetStore.Load(PresetConfigPath), PresetConfigPath, workingFolderPath);
            window.ShowDialog(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load presets:\n{ex.Message}", "Presets", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // true means this click was handled, including declining the confirmation or a validation error.
    private async Task<bool> TryRunDefaultPresetAsync(PresetAction action)
    {
        if (operationCts is not null) return true;
        ActionPreset? preset;
        try { preset = PresetStore.Load(PresetConfigPath).DefaultFor(action)?.Clone(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load presets:\n{ex.Message}", "Presets", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return true;
        }
        if (preset is null) return false;
        ShowWindow(this, EventArgs.Empty);
        string target = preset.ResolveTarget(workingFolderPath);
        if (MessageBox.Show(this, "Review the default preset settings, proceed?\n\n" + preset.Summary(workingFolderPath),
            ActionPreset.Label(action), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return true;
        try { preset.Validate(workingFolderPath); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Preset cannot run. No files changed.\n\n{ex.Message}", "Check preset settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return true;
        }

        bool watching = watcher?.EnableRaisingEvents == true;
        if (watcher is not null) watcher.EnableRaisingEvents = false;
        debounceTimer?.Stop();
        try
        {
            await RunWithProgressAsync(ActionPreset.Label(action), (progress, ct) =>
            {
                // Finish any duration update already in flight before moving files.
                lock (durationUpdateLock)
                {
                    using var transaction = new OperationTransaction();
                    try
                    {
                        preset.Validate(target); // Recheck immediately before execution.
                        string summary = ExecutePreset(preset, target, progress, ct);
                        transaction.Commit(ct);
                        return Task.FromResult<string?>(summary);
                    }
                    catch (Exception ex)
                    {
                        try { transaction.Rollback(); }
                        catch (Exception rollbackError)
                        {
                            throw new IOException($"{ex.Message}\n\nRollback needs attention:\n{rollbackError.Message}", ex);
                        }
                        if (ex is OperationCanceledException)
                            throw new OperationCanceledException("Operation cancelled. All changes from this preset run were reversed.", ex, ct);
                        throw new IOException($"{ex.Message}\n\nAll changes from this preset run were reversed.", ex);
                    }
                }
            }, showSuccessMessage: true);
        }
        finally
        {
            if (watcher is not null) watcher.EnableRaisingEvents = watching;
            if (watching) ScheduleUpdate();
        }
        return true;
    }

    private string ExecutePreset(ActionPreset preset, string target, IOperationProgress progress, CancellationToken ct)
    {
        switch (preset.Action)
        {
            case PresetAction.ShuffleAndName:
                ShuffleAndNameStepTen(target, preset.Start, preset.Padding, preset.Prefix, progress, ct);
                break;
            case PresetAction.NameByTens:
                NameTenSteps(target, preset.Start, progress, ct);
                break;
            case PresetAction.ShuffleRandom:
                ShuffleToRandom(target, preset.RandomLength, progress, ct);
                break;
            case PresetAction.Archive:
                var archive = ClipArchiver.Run(target, preset.Destination, DefaultVideoExts, progress, ct);
                ct.ThrowIfCancellationRequested();
                ThrowScanErrors(archive.Errors);
                return archive.Summary;
            case PresetAction.RemoveDuplicates:
                var duplicates = ClipDuplicateCleaner.Find(target, DefaultVideoExts, progress, ct);
                ThrowScanErrors(duplicates.Errors);
                return ClipDuplicateCleaner.Apply(duplicates, progress, ct, stopBatchOnError: true);
            case PresetAction.IdentifyClip:
                var matches = ClipIdentifier.Find(preset.ReferenceClip, target, DefaultVideoExts, progress, ct);
                ThrowScanErrors(matches.Errors);
                return ClipIdentifier.Apply(matches, preset.ReferenceClip,
                    preset.IdentifyBehavior == IdentifyBehavior.Move ? preset.Destination : null, progress, ct);
            case PresetAction.PrepareBatch:
                string destination = preset.Destination;
                if (preset.CreateBatchFolder)
                {
                    destination = Path.Combine(destination, $"batch_{DateTime.Now:yyyyMMdd-HHmmss}_{Guid.NewGuid():N}");
                    OperationTransaction.CreateDirectory(destination);
                }
                var candidates = preset.Selection == BatchSelection.NumberedMp4 ? null : ListPresetBatchCandidates(target, preset.Destination, preset.Selection, ct);
                return ClipBatchPreparer.Prepare(target, destination, TimeSpan.FromMinutes(preset.DurationMinutes),
                    DefaultVideoExts, progress, ct, candidates: candidates,
                    nameBatch: paths => NamePresetBatch(paths, preset, progress, ct),
                    uniqueDestinationNames: preset.Naming != BatchNaming.Preserve);
        }
        return $"{ActionPreset.Label(preset.Action)} completed.";
    }

    private static void ThrowScanErrors(List<string> errors)
    {
        if (errors.Count > 0) throw new IOException("The operation could not verify all clips." + ClipIdentifier.FormatErrors(errors));
    }

    private static List<string> ListPresetBatchCandidates(string target, string destination, BatchSelection selection, CancellationToken ct)
    {
        var files = new List<string>();
        var folders = new Stack<string>();
        folders.Push(Path.GetFullPath(target));
        while (folders.TryPop(out var folder))
        {
            ct.ThrowIfCancellationRequested();
            ActionPreset.ValidateFolder(folder);
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                ct.ThrowIfCancellationRequested();
                if (!DefaultVideoExts.Contains(Path.GetExtension(path))) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Linked clip cannot be prepared: {path}");
                files.Add(path);
            }
            if (selection != BatchSelection.Archives) continue;
            foreach (string child in Directory.EnumerateDirectories(folder))
            {
                if (Path.GetFileName(child).Equals("Backup", StringComparison.OrdinalIgnoreCase)
                    || ClipIdentifier.SamePath(child, destination)
                    || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                folders.Push(child);
            }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static void NamePresetBatch(IReadOnlyList<string> paths, ActionPreset preset, IOperationProgress progress, CancellationToken ct)
    {
        if (preset.Naming == BatchNaming.Preserve) return;
        var staged = new List<(string Temporary, string Extension)>();
        foreach (string path in paths)
        {
            ct.ThrowIfCancellationRequested();
            string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".preset_" + Guid.NewGuid().ToString("N") + ".pending");
            OperationTransaction.Move(path, temporary);
            staged.Add((temporary, Path.GetExtension(path)));
        }
        long sequence = preset.Start;
        foreach (var item in staged)
        {
            ct.ThrowIfCancellationRequested();
            string name = preset.Naming == BatchNaming.Random ? GenId(Random.Shared, preset.RandomLength)
                : preset.Prefix + sequence.ToString().PadLeft(preset.Padding, '0');
            string final = EnsureUniquePath(Path.Combine(Path.GetDirectoryName(item.Temporary)!, name + item.Extension));
            OperationTransaction.Move(item.Temporary, final);
            sequence += 10;
            progress.SetIndeterminate($"Naming batch clip: {Path.GetFileName(final)}");
        }
    }
}
