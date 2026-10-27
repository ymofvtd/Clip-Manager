using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using VideoTrayApp;

internal static class DuplicationChecks
{
    internal static void Run(string parent, Action<string, uint, byte> write)
    {
        string root = Directory.CreateDirectory(Path.Combine(parent, "duplication-checks")).FullName;
        string a = Directory.CreateDirectory(Path.Combine(root, "A")).FullName;
        string b = Directory.CreateDirectory(Path.Combine(root, "B")).FullName;
        string sub = Directory.CreateDirectory(Path.Combine(a, "nested")).FullName;
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4" };
        string first = Path.Combine(a, "first.mp4"), second = Path.Combine(sub, "second.mp4"), third = Path.Combine(b, "third.mp4");
        write(first, 2, 1); File.Copy(first, second); File.Copy(first, third);
        write(Path.Combine(b, "distinct.mp4"), 2, 2);
        write(Path.Combine(a, "a-only.mp4"), 3, 3); File.Copy(Path.Combine(a, "a-only.mp4"), Path.Combine(a, "a-only-copy.mp4"));
        var progress = new TestProgress();
        var scan = ClipDuplicationSeeker.Find(a, b, true, exts, progress, CancellationToken.None);
        Check(scan.Groups.Count == 1 && scan.Groups[0].Copies.Count == 3 && scan.Groups[0].Copies.Count(c => c.Side == "A") == 2,
            "A/B scan includes every matching copy on either side and excludes single-side groups and different-content clips");
        Check(scan.Groups.SelectMany(g => g.Copies).Where(c => c.RecycleByDefault).Select(c => c.Path).SequenceEqual([third]),
            "Default recycling targets only matching B copies and keeps every A copy");
        var overlap = ClipDuplicationSeeker.Find(a, sub, true, exts, progress, CancellationToken.None);
        Check(overlap.Groups.Count == 1 && overlap.Groups[0].Copies.Count == 2 && overlap.Groups[0].Copies.Count(c => c.Side.StartsWith("A / B")) == 1,
            "Overlapping selections deduplicate paths and label a shared path as one copy");
        Check(overlap.Groups.SelectMany(g => g.Copies).All(c => !c.RecycleByDefault),
            "A shared path inside overlapping roots stays Keep even when it belongs to B");
        string alias = Path.Combine(b, "hardlink.mp4");
        if (!CreateHardLink(alias, third, IntPtr.Zero)) throw new Exception("Cannot create test hard link.");
        scan = ClipDuplicationSeeker.Find(a, b, true, exts, progress, CancellationToken.None);
        Check(scan.Groups.Single().Copies.Count(c => c.HardLink) == 2,
            "Hard-link aliases are detected by volume/file identity and labeled as the same underlying file");
        Check(scan.Groups.SelectMany(g => g.Copies).All(c => !c.RecycleByDefault),
            "B hard-link aliases stay Keep instead of being preselected for recycling");
        bool allRejected = false;
        try { ClipDuplicationSeeker.Apply(scan, scan.Groups.Single().Copies.Select(c => c.Path).ToHashSet(), progress, CancellationToken.None); }
        catch (ArgumentException) { allRejected = true; }
        Check(allRejected && File.Exists(first), "Deleting every group member is rejected before changing files");
        bool aliasRejected = false;
        try { ClipDuplicationSeeker.Apply(scan, new HashSet<string> { alias }, progress, CancellationToken.None); }
        catch (ArgumentException) { aliasRejected = true; }
        Check(aliasRejected && File.Exists(alias), "Hard-link aliases cannot be selected as reclaimable duplicate content");
        File.Delete(alias);
        scan = ClipDuplicationSeeker.Find(a, b, true, exts, progress, CancellationToken.None);
        // Selected copies may be on either side; no alphabetical or directory policy is imposed.
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first, second };
        write(third, 2, 9);
        var result = ClipDuplicationSeeker.Apply(scan, selected, progress, CancellationToken.None, _ => throw new Exception("Must not recycle"));
        Check(result.Recycled == 0 && result.Untouched == 2 && File.Exists(first) && File.Exists(second),
            "A changed keeper leaves the entire selected group untouched");
        File.Copy(first, third, true);
        scan = ClipDuplicationSeeker.Find(a, b, true, exts, progress, CancellationToken.None);
        write(first, 2, 8);
        int attempts = 0;
        result = ClipDuplicationSeeker.Apply(scan, selected, progress, CancellationToken.None, path => { attempts++; File.Delete(path); });
        Check(result.Recycled == 1 && File.Exists(first) && !File.Exists(second) && attempts == 1,
            "Changed deletion targets stay untouched while other explicitly selected copies may proceed");
        File.Copy(third, second);
        File.Copy(third, first, true);
        scan = ClipDuplicationSeeker.Find(a, b, true, exts, progress, CancellationToken.None);
        result = ClipDuplicationSeeker.Apply(scan, selected, progress, CancellationToken.None, _ => throw new IOException("Injected Recycle Bin unavailable"));
        Check(result.Recycled == 0 && File.Exists(first) && File.Exists(second), "Recycle Bin failures leave selected files untouched with no permanent-delete fallback");
        using var cts = new CancellationTokenSource();
        result = ClipDuplicationSeeker.Apply(scan, selected, progress, cts.Token, path => { File.Delete(path); cts.Cancel(); });
        Check(result.Cancelled && result.Recycled == 1 && result.Untouched == 1 && File.Exists(third),
            "Cancellation reports completed recycling separately from untouched copies and retains the keeper");
        var live = ClipDuplicationSeeker.Find(a, b, true, exts, progress, CancellationToken.None);
        result = ClipDuplicationSeeker.Apply(live, new HashSet<string> { second }, progress, CancellationToken.None);
        Check(result.Recycled == 1 && result.Errors.Count == 0 && File.Exists(third) && !File.Exists(second),
            "Actual selected-copy recycling works while verified kept/selected handles exclude content writes");
        string recycleTest = Path.Combine(root, "recycle-only.mp4"); write(recycleTest, 2, 50);
        RecycleBin.Recycle(recycleTest);
        Check(!File.Exists(recycleTest), "Windows IFileOperation performs a real Recycle Bin-only operation on a disposable clip");
        Exception? uiError = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var review = new DuplicationReviewForm(scan);
                review.CreateControl();
                var grid = Descendants(review).OfType<DataGridView>().Single();
                Check(grid.Rows.Cast<DataGridViewRow>().Where(row => Equals(row.Cells[0].Value, "Recycle"))
                    .Select(row => ((DuplicationCopy)row.Tag!).Path).SequenceEqual([third]) && review.SelectedPaths.Count == 0,
                    "Legacy review marks matching B copies Recycle while keeping A and waiting for confirmation");
                using var coordinator = new OperationCoordinator();
                using var worker = new ArchiveWorker(coordinator, exts, Path.Combine(root, "ui-settings"));
                using var archive = new ArchiveForm(worker, new ArchiveSettings { Sources = [a], Destination = b }, Path.Combine(root, "ui-settings"));
                archive.CreateControl();
                Check(Descendants(archive).OfType<Button>().Any(button => button.Text == "Resume"), "Archive status/configuration window exposes lifecycle controls");
                if (Environment.GetEnvironmentVariable("CLIP_MANAGER_UI_QA") is { Length: > 0 } qa)
                {
                    Directory.CreateDirectory(qa);
                    foreach (var form in new Form[] { review, archive })
                    {
                        form.Show(); form.PerformLayout(); Application.DoEvents();
                        using var bitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                        bitmap.Save(Path.Combine(qa, form is ArchiveForm ? "archive.png" : "duplications.png")); form.Hide();
                    }
                }
            }
            catch (Exception ex) { uiError = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (uiError is not null) throw uiError;
        SelectionUi(root, write);
    }
    private static void SelectionUi(string parent, Action<string, uint, byte> write)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles(); Control.CheckForIllegalCrossThreadCalls = true;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                string data = Directory.CreateDirectory(Path.Combine(parent, "selection-ui-data")).FullName;
                string a = Directory.CreateDirectory(Path.Combine(parent, "selection-A")).FullName;
                string b = Directory.CreateDirectory(Path.Combine(parent, "selection-B")).FullName;
                string nestedA = Directory.CreateDirectory(Path.Combine(a, "nested")).FullName;
                string nestedB = Directory.CreateDirectory(Path.Combine(b, "nested")).FullName;
                string first = Path.Combine(a, "first.mp4"), extraA = Path.Combine(nestedA, "extra.mp4"), otherA = Path.Combine(a, "other.mp4");
                string firstB = Path.Combine(b, "first-copy.mp4"), keepB = Path.Combine(nestedB, "keep-copy.mp4"), otherB = Path.Combine(b, "other-copy.mp4");
                string uniqueB = Path.Combine(b, "unique.mp4");
                write(first, 2, 61); File.Copy(first, extraA); File.Copy(first, firstB); File.Copy(first, keepB);
                write(otherA, 3, 62); File.Copy(otherA, otherB); write(uniqueB, 4, 63);
                string[] aPaths = [first, extraA, otherA], bDuplicates = [firstB, keepB, otherB];
                new UiPreferences { SetupDismissed = true, CloseToTray = false }.Save(data);
                using var shell = new MainShell(data); shell.Show(); Application.DoEvents();
                shell.OpenTool(PresetAction.SeekDuplications);
                var workspace = Descendants(shell).OfType<WorkflowWorkspace>().Single(w => w.Action == PresetAction.SeekDuplications);
                void Configure(string rootA, string rootB) => workspace.ApplyPreset(new ActionPreset
                { Action = PresetAction.SeekDuplications, Target = rootA, DirectoryB = rootB, IncludeSubfolders = true, UseWorkingFolder = false });
                Configure(a, b); Pump(workspace.AnalyzeAsync());
                var list = Descendants(workspace).OfType<FileContextList>().Single();
                Check(list.Rows.Count == 6 && list.SelectedKeys.SetEquals(bDuplicates)
                    && list.Rows.Where(r => aPaths.Contains(r.Path)).All(r => !list.SelectedKeys.Contains(r.Key))
                    && aPaths.Concat(bDuplicates).Append(uniqueB).All(File.Exists),
                    "Workspace automatically marks B duplicates across groups and subfolders without changing files or selecting unique B clips");
                Check(list.Table.Rows.Cast<DataGridViewRow>().All(row => Equals(row.Cells[1].Value,
                    bDuplicates.Contains(list.Rows[row.Index].Key) ? "Recycle" : "Keep")),
                    "The visible Action cells show the automatic A Keep and B Recycle choices");
                list.SetFilter("keep-copy.mp4"); list.Table.CurrentCell = list.Table.Rows[0].Cells[1];
                typeof(DataGridView).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(list.Table, [new KeyEventArgs(Keys.Space)]);
                Check(list.SelectedKeys.SetEquals([firstB, otherB]), "Individual B copies can be changed back to Keep");
                Pump(workspace.AnalyzeAsync());
                Check(list.SelectedKeys.SetEquals([firstB, otherB]) && list.Table.RowCount == 1,
                    "Refreshing preserves a manual Keep override and hidden recycle selections for unchanged files");
                Configure(b, a); Pump(workspace.AnalyzeAsync());
                Check(list.SelectedKeys.SetEquals(aPaths), "Swapping directories applies the defaults to their new A and B roles");
                Configure(a, b); Pump(workspace.AnalyzeAsync());
                Check(list.SelectedKeys.SetEquals(bDuplicates), "Restoring directories reapplies defaults when their roles change");
                list.Table.CurrentCell = list.Table.Rows[0].Cells[1];
                typeof(DataGridView).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(list.Table, [new KeyEventArgs(Keys.Space)]);
                void Run(DialogResult response)
                {
                    bool reviewed = false;
                    using var confirm = new System.Windows.Forms.Timer { Interval = 10 };
                    confirm.Tick += (_, _) =>
                    {
                        if (Application.OpenForms.OfType<ReviewConfirmation>().FirstOrDefault() is not { } dialog) return;
                        reviewed = true;
                        string details = Descendants(dialog).OfType<TextBox>().Single().Text;
                        Check(details.Contains(firstB) && details.Contains(otherB) && !details.Contains(keepB)
                            && details.Contains(first) && details.Contains(otherA),
                            "Final confirmation includes hidden B targets and their A keepers, excluding the manually kept B copy");
                        dialog.DialogResult = response;
                    };
                    confirm.Start();
                    Pump((Task)typeof(WorkflowWorkspace).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(workspace, null)!);
                    Check(reviewed, "Automatic defaults still require final exact-file confirmation");
                }
                Run(DialogResult.Cancel);
                Check(aPaths.Concat(bDuplicates).Append(uniqueB).All(File.Exists) && list.SelectedKeys.SetEquals([firstB, otherB]),
                    "Cancelling confirmation preserves all clips and the automatic and manual choices");
                Run(DialogResult.OK);
                Check(!File.Exists(firstB) && !File.Exists(otherB) && aPaths.Append(keepB).Append(uniqueB).All(File.Exists),
                    "Confirmed defaults recycle only selected B duplicates and preserve all A clips, unique B clips and manual Keep overrides");
                shell.Close(); while (!shell.IsDisposed) { Application.DoEvents(); Thread.Sleep(2); }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw new Exception("Duplication selection UI check failed.", error);
    }
    private static void Pump(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (timeout.ElapsedMilliseconds > 15000) throw new Exception("Duplication selection UI operation timed out.");
            Application.DoEvents(); Thread.Sleep(2);
        }
        task.GetAwaiter().GetResult(); Application.DoEvents();
    }
    private static IEnumerable<Control> Descendants(Control control)
    { foreach (Control child in control.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existing, IntPtr security);
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
}
