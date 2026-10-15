using System.Diagnostics;
using System.Reflection;
using VideoTrayApp;

internal static class IdentifySelectionChecks
{
    internal static void Run(string parent, Action<string, uint, byte> write)
    {
        Plans(parent, write);
        Ui(parent, write);
    }

    private static void Plans(string parent, Action<string, uint, byte> write)
    {
        string source = Directory.CreateDirectory(Path.Combine(parent, "identify-selection")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(parent, "identify-isolated")).FullName;
        string reference = Path.Combine(source, "reference.mp4"), first = Path.Combine(source, "first.mp4"), second = Path.Combine(source, "second.mp4");
        write(reference, 2, 71); File.Copy(reference, first); File.Copy(reference, second);
        var ct = CancellationToken.None;
        var progress = new TestProgress();
        IdentifyClipScan Scan() => new(PlannedFile.Read(reference, ct), new[] { first, second }.Where(File.Exists).Select(path => PlannedFile.Read(path, ct)).ToArray());
        var scan = Scan();
        File.WriteAllText(Path.Combine(destination, "first.mp4"), "existing destination");
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first };
        var move = scan.Prepare(selected, destination, progress, ct);
        Check(move.ReviewRows().Single().Proposed == Path.Combine(destination, "first__1.mp4"), "Identify selection freezes collision destinations for only selected matches");
        move.Execute(progress, ct);
        Check(!File.Exists(first) && File.Exists(second) && File.Exists(reference)
            && File.ReadAllText(Path.Combine(destination, "first.mp4")) == "existing destination", "Identify move isolates only selected clips and preserves the reference, unselected clips and existing files");

        selected = new(StringComparer.OrdinalIgnoreCase) { second };
        var delete = Scan().Prepare(selected, null, progress, ct);
        write(reference, 2, 72);
        Reject(() => delete.Execute(progress, ct), "Changing the reference after confirmation blocks selected recycling");
        Check(File.Exists(second), "A changed reference leaves the selected match untouched");
        File.Copy(second, reference, true);
        scan = Scan();
        write(second, 2, 73);
        Reject(() => scan.Prepare(selected, destination, progress, ct), "Changing a selected match after scanning blocks review preparation");
        File.Copy(reference, second, true);
        move = Scan().Prepare(selected, destination, progress, ct);
        File.WriteAllText(move.Selection.Changes.Single().Destination, "occupied after review");
        Reject(() => move.Execute(progress, ct), "Occupying a reviewed identify destination blocks the transaction");
        Check(File.Exists(second), "Destination conflicts preserve selected matches");

        scan = new(PlannedFile.Read(reference, ct), [PlannedFile.Read(reference, ct)]);
        Reject(() => scan.Prepare(new HashSet<string> { reference }, null, progress, ct), "Identify action planning rejects the reference even if supplied as a match");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool cancelled = false;
        try { Scan().Prepare(selected, null, progress, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && File.Exists(second), "Cancelled identify selection preparation preserves files");
    }

    private static void Ui(string parent, Action<string, uint, byte> write)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                Control.CheckForIllegalCrossThreadCalls = true;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                string data = Directory.CreateDirectory(Path.Combine(parent, "identify-ui-data")).FullName;
                string source = Directory.CreateDirectory(Path.Combine(parent, "identify-ui-source")).FullName;
                string destination = Directory.CreateDirectory(Path.Combine(parent, "identify-ui-isolated")).FullName;
                string reference = Path.Combine(source, "reference.mp4"), first = Path.Combine(source, "first.mp4"), second = Path.Combine(source, "second.mp4");
                write(reference, 2, 74); File.Copy(reference, first); File.Copy(reference, second);
                new UiPreferences { SetupDismissed = true, CloseToTray = false }.Save(data);
                using var shell = new MainShell(data); shell.Show(); Application.DoEvents();
                shell.OpenTool(PresetAction.IdentifyClip);
                var workspace = Descendants(shell).OfType<WorkflowWorkspace>().Single(w => w.Action == PresetAction.IdentifyClip);
                workspace.ApplyPreset(new ActionPreset { Action = PresetAction.IdentifyClip, Target = source, ReferenceClip = reference, UseWorkingFolder = false });
                Pump(workspace.AnalyzeAsync());
                var list = Descendants(workspace).OfType<FileContextList>().Single();
                Button Button(string text) => Descendants(workspace).OfType<Button>().Single(b => b.Text == text);
                Check(list.Rows.Count == 2 && list.Rows.All(r => r.Selectable) && list.Table.Columns["Action"]!.HeaderText == "Select"
                    && Button("Move selected…").Visible && !Button("Move selected…").Enabled
                    && Button("Delete selected…").Visible && !Button("Delete selected…").Enabled,
                    "Identify results expose selectable matches and visible move/delete buttons disabled until selection");
                list.SetFilter("first.mp4");
                typeof(DataGridView).GetMethod("OnCellClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(list.Table, [new DataGridViewCellEventArgs(1, 0)]);
                Check(list.SelectedKeys.SetEquals([first]) && Button("Move selected…").Enabled, "Clicking an identify Select cell selects the row and enables actions");
                typeof(DataGridView).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(list.Table, [new KeyEventArgs(Keys.Space)]);
                Check(list.SelectedKeys.Count == 0, "Space toggles identify row selection");
                Button("Select all visible").PerformClick(); list.SetFilter("second.mp4"); Button("Select all visible").PerformClick();
                Check(list.SelectedKeys.SetEquals([first, second]), "Identify Select all visible preserves selected matches hidden by filters");
                Button("Clear selection").PerformClick();
                Check(list.SelectedKeys.Count == 0 && !Button("Delete selected…").Enabled, "Identify Clear selection also clears hidden matches and disables actions");
                list.SetFilter("first.mp4"); Button("Select all visible").PerformClick(); list.SetFilter("second.mp4");
                foreach (var size in new[] { new Size(1180, 760), new Size(640, 480) })
                {
                    shell.ClientSize = size; Application.DoEvents();
                    Check(list.Visible && list.Width > 300 && Button("Select all visible").Visible && Button("Move selected…").Visible,
                        $"Identify results and bulk actions remain reachable at {size.Width}x{size.Height}");
                }
                if (Environment.GetEnvironmentVariable("CLIP_IDENTIFY_SCREENSHOT") is { Length: > 0 } screenshot)
                {
                    shell.ClientSize = new Size(1180, 760); list.SetFilter(""); Application.DoEvents();
                    using var bitmap = new Bitmap(shell.Width, shell.Height);
                    shell.DrawToBitmap(bitmap, new Rectangle(Point.Empty, shell.Size)); bitmap.Save(screenshot);
                    list.SetFilter("second.mp4");
                }
                void Act(string? target, DialogResult response)
                {
                    bool reviewed = false;
                    using var confirm = new System.Windows.Forms.Timer { Interval = 10 };
                    confirm.Tick += (_, _) =>
                    {
                        if (Application.OpenForms.OfType<ReviewConfirmation>().FirstOrDefault() is not { } dialog) return;
                        reviewed = true;
                        string details = Descendants(dialog).OfType<TextBox>().Single().Text;
                        string expected = list.SelectedKeys.Single();
                        Check(details.Contains(expected) && !details.Contains("\n" + reference), "Identify confirmation includes the exact selected path, including hidden selection");
                        dialog.DialogResult = response;
                    };
                    confirm.Start(); Pump(workspace.ActOnIdentifiedAsync(target)); confirm.Stop();
                    Check(reviewed, "Identify bulk actions require exact-file confirmation");
                }
                Act(null, DialogResult.Cancel);
                Check(File.Exists(first) && File.Exists(second) && list.SelectedKeys.SetEquals([first]), "Cancelling identify Delete retains clips and selection");
                Act(destination, DialogResult.OK);
                Check(!File.Exists(first) && File.Exists(Path.Combine(destination, "first.mp4")) && File.Exists(second) && File.Exists(reference)
                    && list.Rows.Count == 1 && list.SelectedKeys.Count == 0, "Confirmed identify Move acts on hidden selected clips only and removes completed rows");
                Button("Select all visible").PerformClick(); Act(null, DialogResult.OK);
                Check(!File.Exists(second) && File.Exists(reference) && list.Rows.Count == 0 && !Button("Delete selected…").Enabled,
                    "Confirmed identify Delete recycles the selected match, preserves the reference and clears completed results");

                File.Copy(reference, first); File.WriteAllBytes(Path.Combine(source, "invalid.mp4"), new byte[new FileInfo(reference).Length]);
                Pump(workspace.AnalyzeAsync()); Button("Select all visible").PerformClick();
                Check(list.Rows.Count == 1 && !Button("Move selected…").Enabled && !Button("Delete selected…").Enabled,
                    "Incomplete identify scans remain inspectable but block all bulk changes");
                shell.Close();
                while (!shell.IsDisposed) { Application.DoEvents(); Thread.Sleep(2); }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw new Exception("Identify selection UI check failed.", error);
    }

    private static void Pump(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (timeout.ElapsedMilliseconds > 15000) throw new Exception("Identify selection UI operation timed out.");
            Application.DoEvents(); Thread.Sleep(2);
        }
        task.GetAwaiter().GetResult(); Application.DoEvents();
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static void Reject(Action action, string description)
    {
        bool rejected = false;
        try { action(); } catch (IOException) { rejected = true; }
        Check(rejected, description);
    }
    private static void Check(bool condition, string description)
    { if (!condition) throw new Exception("FAIL: " + description); Console.WriteLine("PASS: " + description); }
}
