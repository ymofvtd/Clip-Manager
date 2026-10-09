using System.Security.Cryptography;
using VideoTrayApp;

internal static class PresetChecks
{
    internal static void Run(string parent, Action<string, uint, byte> writeVideo)
    {
        string root = Directory.CreateDirectory(Path.Combine(parent, "preset-checks")).FullName;
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
        var progress = new TestProgress();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".avi" };
        var store = new PresetStore();
        string config = Path.Combine(root, "presets.json");
        var preset = new ActionPreset { Action = PresetAction.PrepareBatch, Name = "Random 30m archives", UseWorkingFolder = false,
            Target = source, Destination = destination, DurationMinutes = 30, Selection = BatchSelection.Archives, Naming = BatchNaming.NumberByTens };
        store.Presets.Add(preset);
        store.Defaults[preset.Action] = preset.Id;
        store.Save(config);
        store = PresetStore.Load(config);
        Check(store.DefaultFor(preset.Action)?.Selection == BatchSelection.Archives && store.DefaultFor(preset.Action)?.DurationMinutes == 30,
            "Preset settings and action default survive restart");
        var edited = store.Presets[0].Clone();
        edited.Name = "Edited";
        edited.DurationMinutes = 45;
        store.Presets[0] = edited;
        store.Save(config);
        Check(PresetStore.Load(config).DefaultFor(preset.Action)?.Name == "Edited" && store.DefaultFor(preset.Action)?.DurationMinutes == 45,
            "Editing a preset preserves its default identity");
        store.Defaults.Remove(preset.Action);
        store.Save(config);
        Check(PresetStore.Load(config).DefaultFor(preset.Action) is null, "Always ask persists as the action default");
        store.Presets.Clear();
        store.Save(config);
        Check(PresetStore.Load(config).Presets.Count == 0, "Deleted presets stay deleted after restart");
        preset.Validate(source);
        var missing = preset.Clone();
        missing.Destination = Path.Combine(root, "missing");
        ExpectFailure(() => missing.Validate(source));
        Check(!Directory.Exists(missing.Destination), "Missing preset destinations are rejected without recreating them");
        RunWindowChecks(preset, config, source);
        RunBatchDestinationChecks(root, writeVideo);

        string first = Path.Combine(source, "first.avi");
        string second = Path.Combine(source, "second.avi");
        writeVideo(first, 2, 51);
        writeVideo(second, 2, 52);
        var before = Snapshot(source, destination);
        using (var tx = new OperationTransaction())
        {
            string temp = Path.Combine(source, "temporary.pending");
            OperationTransaction.Move(first, temp);
            OperationTransaction.Move(second, first);
            OperationTransaction.Move(temp, second);
            OperationTransaction.Recycle(first);
            OperationTransaction.CreateDirectory(Path.Combine(source, "Backup"));
            OperationTransaction.Copy(second, Path.Combine(source, "Backup", "first.avi"));
            tx.Rollback();
        }
        Check(before.SequenceEqual(Snapshot(source, destination)), "Rollback restores two-pass names, staged deletions, backups and newly created folders");
        using (var tx = new OperationTransaction())
        {
            ExpectFailure(() => OperationTransaction.Copy(first, second));
            tx.Rollback();
        }
        Check(before.SequenceEqual(Snapshot(source, destination)), "A failed backup copy never deletes an existing collision");

        string blockedRoot = Directory.CreateDirectory(Path.Combine(root, "blocked-rollback")).FullName;
        string blockedOriginal = Path.Combine(blockedRoot, "original.avi");
        string movedOriginal = Path.Combine(blockedRoot, "moved.avi");
        string retainedBackup = Path.Combine(blockedRoot, "backup.avi");
        writeVideo(blockedOriginal, 2, 53);
        using (var tx = new OperationTransaction())
        {
            OperationTransaction.Copy(blockedOriginal, retainedBackup);
            OperationTransaction.Move(blockedOriginal, movedOriginal);
            File.WriteAllText(blockedOriginal, "An external process created this collision");
            ExpectFailure(tx.Rollback);
        }
        Check(File.Exists(retainedBackup) && File.Exists(movedOriginal)
            && File.ReadAllText(blockedOriginal).StartsWith("An external process"),
            "Blocked rollback retains backups and moved content without overwriting external files");

        using (var tx = new OperationTransaction())
        {
            OperationTransaction.Recycle(first);
            OperationTransaction.Recycle(second);
            int calls = 0;
            ExpectFailure(() => tx.Commit(CancellationToken.None, path =>
            {
                if (++calls == 2) throw new IOException("Injected Recycle Bin failure");
                File.Delete(path);
            }));
            tx.Rollback();
        }
        Check(before.SequenceEqual(Snapshot(source, destination)), "A failure midway through recycling restores even the already recycled content");

        using var cancel = new CancellationTokenSource();
        using (var tx = new OperationTransaction())
        {
            var result = ClipArchiver.Run(source, destination, extensions, new TestProgress
            {
                OnMessage = message => { if (message.StartsWith("Moving second")) cancel.Cancel(); }
            }, cancel.Token);
            Check(result.Cancelled && result.Moved == 1, "Archive cancellation reaches a partially moved preset run");
            tx.Rollback();
        }
        Check(before.SequenceEqual(Snapshot(source, destination)), "Cancelled archive preset restores original source and destination");

        string batchSource = Directory.CreateDirectory(Path.Combine(root, "batch-source")).FullName;
        string batchDest = Directory.CreateDirectory(Path.Combine(root, "batch-dest")).FullName;
        writeVideo(Path.Combine(batchSource, "10.avi"), 2, 61);
        File.Copy(Path.Combine(batchSource, "10.avi"), Path.Combine(batchDest, "keeper.avi"));
        writeVideo(Path.Combine(batchSource, "20.avi"), 2, 62);
        writeVideo(Path.Combine(batchSource, "30.avi"), 2, 63);
        var batchBefore = Snapshot(batchSource, batchDest);
        using (var tx = new OperationTransaction())
        {
            ExpectFailure(() => ClipBatchPreparer.Prepare(batchSource, batchDest, TimeSpan.FromSeconds(10), extensions,
                new TestProgress { OnMessage = message => { if (message == "Moving 30.avi...") throw new IOException("Injected move failure"); } },
                CancellationToken.None, ".avi", new PreserveOrderRandom()));
            tx.Rollback();
        }
        Check(batchBefore.SequenceEqual(Snapshot(batchSource, batchDest)), "Batch failure restores removed duplicates, earlier moves, backups and original filenames");

        string archiveChild = Directory.CreateDirectory(Path.Combine(batchSource, "archive")).FullName;
        writeVideo(Path.Combine(archiveChild, "20.avi"), 2, 64);
        using (var tx = new OperationTransaction())
        {
            ClipBatchPreparer.Prepare(batchSource, batchDest, TimeSpan.FromSeconds(20), extensions, progress,
                CancellationToken.None, candidates: [Path.Combine(batchSource, "20.avi"), Path.Combine(archiveChild, "20.avi")]);
            tx.Commit(CancellationToken.None);
        }
        Check(File.Exists(Path.Combine(batchDest, "20.avi")) && File.Exists(Path.Combine(batchDest, "20__1.avi"))
            && Directory.GetFiles(Path.Combine(batchSource, "Backup")).Length == 2,
            "Archive batch candidates with colliding filenames are moved and backed up without overwriting");

        using (var tx = new OperationTransaction())
        {
            OperationTransaction.Recycle(first);
            tx.Commit(CancellationToken.None);
        }
        Check(!File.Exists(first) && Directory.GetFiles(source).SequenceEqual([second]), "Successful preset commit recycles staged removal and cleans recovery copies");
    }

    private static List<string> Snapshot(params string[] roots) => roots.SelectMany(root =>
        Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Select(path => "DIR:" + path)
        .Concat(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(path => path + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))))
        .Order(StringComparer.OrdinalIgnoreCase).ToList();

    private static void RunBatchDestinationChecks(string root, Action<string, uint, byte> writeVideo)
    {
        string parent = Directory.CreateDirectory(Path.Combine(root, "automatic-batches")).FullName;
        var preset = new ActionPreset { Action = PresetAction.PrepareBatch, UseWorkingFolder = false,
            Target = parent, Destination = parent };
        preset.Validate(parent);
        var fresh = BatchDestinationResolver.Resolve(preset, parent);
        Check(fresh.Create && fresh.Path == Path.Combine(parent, "batch_1") && !Directory.Exists(fresh.Path),
            "Same target/destination is allowed; resolving a missing batch plans batch_1 without creating it");
        File.WriteAllText(fresh.Path, "occupied filename");
        Directory.CreateDirectory(Path.Combine(parent, "batch_2"));
        var fallback = BatchDestinationResolver.Resolve(preset, parent);
        Check(fallback.Create && fallback.Path == Path.Combine(parent, "batch_3"),
            "Automatic batch numbering skips both existing folders and files");
        using (var tx = new OperationTransaction())
        {
            OperationTransaction.CreateDirectory(fallback.Path);
            tx.Rollback();
        }
        Check(!Directory.Exists(fallback.Path), "A failed preset removes its automatically created fallback folder");

        preset.BatchFolderName = "My compilation";
        var custom = BatchDestinationResolver.Resolve(preset, parent);
        Check(custom.Create && Path.GetFileName(custom.Path) == "My compilation", "A preset can name its new batch folder");
        Directory.CreateDirectory(custom.Path);
        Check(Path.GetFileName(BatchDestinationResolver.Resolve(preset, parent).Path) == "My compilation_1",
            "A custom batch name collision plans a fresh folder without reusing existing content");
        var store = new PresetStore { Presets = [preset] };
        string config = Path.Combine(root, "named-batch-presets.json");
        store.Save(config);
        Check(PresetStore.Load(config).Presets.Single().BatchFolderName == "My compilation", "Custom batch folder names survive restart");
        foreach (string invalid in new[] { "..", "../outside", "nested\\batch", "CON", "NUL.txt", "bad." })
        {
            bool rejected = false;
            try { BatchDestinationResolver.ValidateName(invalid); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, $"Unsafe batch folder name is rejected: {invalid}");
        }

        // The starter folder can appear after the preset has already been saved.
        Directory.CreateDirectory(Path.Combine(parent, "c_9"));
        Directory.CreateDirectory(Path.Combine(parent, "c_58"));
        string current = Directory.CreateDirectory(Path.Combine(parent, "c_59")).FullName;
        File.WriteAllText(Path.Combine(parent, "c_999"), "not a folder");
        var reused = BatchDestinationResolver.Resolve(PresetStore.Load(config).Presets.Single(), parent);
        Check(!reused.Create && reused.Path == current,
            "Every preset automatically finds the highest numbered c_ folder created after saving");
        Directory.CreateDirectory(Path.Combine(parent, "c_100"));
        Check(Path.GetFileName(BatchDestinationResolver.Resolve(preset, parent).Path) == "c_100",
            "c_ folders are ranked numerically rather than alphabetically");
        Directory.Delete(Path.Combine(parent, "c_100"));

        writeVideo(Path.Combine(current, "starter.avi"), 2, 71);
        writeVideo(Path.Combine(parent, "10.avi"), 2, 72);
        writeVideo(Path.Combine(parent, "20.avi"), 2, 73);
        string starterHash = ClipIdentifier.ReadIdentity(Path.Combine(current, "starter.avi"), CancellationToken.None).Hash;
        var before = Snapshot(parent);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".avi" };
        using (var tx = new OperationTransaction())
        {
            ExpectFailure(() => ClipBatchPreparer.Prepare(parent, reused.Path, TimeSpan.FromSeconds(6), extensions,
                new TestProgress { OnMessage = message => { if (message == "Moving 20.avi...") throw new IOException("Injected refill failure"); } },
                CancellationToken.None, ".avi", new PreserveOrderRandom()));
            tx.Rollback();
        }
        Check(before.SequenceEqual(Snapshot(parent)), "Failed refill of an existing c_ folder restores starter clips, source files and backups");
        using (var tx = new OperationTransaction())
        {
            reused.Validate(parent, preset.Destination);
            ClipBatchPreparer.Prepare(parent, reused.Path, TimeSpan.FromSeconds(6), extensions,
                new TestProgress(), CancellationToken.None, ".avi", new PreserveOrderRandom());
            tx.Commit(CancellationToken.None);
        }
        Check(Directory.GetFiles(current, "*.avi").Length == 3
            && ClipIdentifier.ReadIdentity(Path.Combine(current, "starter.avi"), CancellationToken.None).Hash == starterHash
            && !Directory.Exists(fallback.Path),
            "Prepare Batch refills the chosen c_ folder to the requested total duration while preserving starter clips");

        Directory.Delete(Path.Combine(parent, "c_9"));
        Directory.Delete(Path.Combine(parent, "c_58"));
        Directory.Move(current, Path.Combine(parent, "finished_59"));
        ExpectFailure(() => reused.Validate(parent, preset.Destination));
        preset.BatchFolderName = "";
        Check(BatchDestinationResolver.Resolve(preset, parent).Create, "When c_ folders disappear, the next run automatically plans a new batch");
        Directory.CreateDirectory(fallback.Path);
        ExpectFailure(() => fallback.Validate(parent, preset.Destination));
        Check(Directory.Exists(fallback.Path), "A folder created after confirmation is rejected rather than silently reused");

        // The removed checkbox is ignored in older files; automatic destination selection applies.
        File.WriteAllText(config, System.Text.Json.JsonSerializer.Serialize(new
        {
            Presets = new[] { new { Id = "legacy", Name = "Legacy batch", Action = PresetAction.PrepareBatch,
                Target = parent, UseWorkingFolder = false, Destination = parent, CreateBatchFolder = false } },
            Defaults = new Dictionary<PresetAction, string>()
        }));
        var legacy = PresetStore.Load(config).Presets.Single();
        legacy.Validate(parent);
        Check(BatchDestinationResolver.Resolve(legacy, parent).Create, "Existing presets adopt automatic subfolder creation without the old checkbox");
    }

    private static void RunWindowChecks(ActionPreset preset, string config, string workingFolder)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var store = new PresetStore { Presets = [preset.Clone()], Defaults = new() { [preset.Action] = preset.Id } };
                using var manager = new PresetsForm(store, config, workingFolder);
                var tabs = manager.Controls.OfType<TabControl>().Single();
                Check(tabs.TabPages.Count == 7, "Preset manager opens a tab for each clip action");
                var page = tabs.TabPages[0];
                var list = Descendants(page).OfType<ListBox>().Single();
                list.SelectedIndex = 1;
                var edit = Descendants(page).OfType<Button>().Single(b => b.Text == "Edit");
                Check(edit.Enabled && Descendants(page).OfType<TextBox>().Single().Text.Contains("30 minutes"),
                    "Selecting a saved preset shows its settings and enables editing");
                list.SelectedIndex = 0;
                // Invoke the real UI handler without displaying a modal dialog.
                var button = Descendants(page).OfType<Button>().Single(b => b.Text == "Set as default");
                typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(button, [EventArgs.Empty]);
                Check(PresetStore.Load(config).DefaultFor(preset.Action) is null && !edit.Enabled,
                    "Selecting Always ask and Set as default persists manual mode from the window");
                foreach (var action in Enum.GetValues<PresetAction>())
                {
                    using var editor = new PresetEditorForm(new ActionPreset { Action = action, Target = workingFolder }, workingFolder);
                    editor.CreateControl();
                    Check(Descendants(editor).OfType<Button>().Any(b => b.Text == "Save preset"), $"{ActionPreset.Label(action)} preset editor constructs successfully");
                }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Exception("Preset window checks failed", error);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new Exception("Expected an I/O failure.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
