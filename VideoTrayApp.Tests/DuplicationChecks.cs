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
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".avi" };
        string first = Path.Combine(a, "first.avi"), second = Path.Combine(sub, "second.avi"), third = Path.Combine(b, "third.avi");
        write(first, 2, 1); File.Copy(first, second); File.Copy(first, third);
        write(Path.Combine(b, "distinct.avi"), 2, 2);
        write(Path.Combine(a, "a-only.avi"), 3, 3); File.Copy(Path.Combine(a, "a-only.avi"), Path.Combine(a, "a-only-copy.avi"));
        var progress = new TestProgress();
        var scan = ClipDuplicationSeeker.Find(a, b, true, exts, progress, CancellationToken.None);
        Check(scan.Groups.Count == 1 && scan.Groups[0].Copies.Count == 3 && scan.Groups[0].Copies.Count(c => c.Side == "A") == 2,
            "A/B scan includes every matching copy on either side and excludes single-side groups and different-content clips");
        var overlap = ClipDuplicationSeeker.Find(a, sub, true, exts, progress, CancellationToken.None);
        Check(overlap.Groups.Count == 1 && overlap.Groups[0].Copies.Count == 2 && overlap.Groups[0].Copies.Count(c => c.Side.StartsWith("A / B")) == 1,
            "Overlapping selections deduplicate paths and label a shared path as one copy");
        string alias = Path.Combine(b, "hardlink.avi");
        if (!CreateHardLink(alias, third, IntPtr.Zero)) throw new Exception("Cannot create test hard link.");
        scan = ClipDuplicationSeeker.Find(a, b, true, exts, progress, CancellationToken.None);
        Check(scan.Groups.Single().Copies.Count(c => c.HardLink) == 2,
            "Hard-link aliases are detected by volume/file identity and labeled as the same underlying file");
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
        string recycleTest = Path.Combine(root, "recycle-only.avi"); write(recycleTest, 2, 50);
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
                Check(grid.Rows.Cast<DataGridViewRow>().All(row => Equals(row.Cells[0].Value, "Keep")) && review.SelectedPaths.Count == 0,
                    "Review table defaults every individual path to Keep with no preselected deletions");
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
    }
    private static IEnumerable<Control> Descendants(Control control)
    { foreach (Control child in control.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existing, IntPtr security);
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
}
