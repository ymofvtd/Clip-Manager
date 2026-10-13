using System.ComponentModel;
using System.Reflection;
using VideoTrayApp;

internal static class MainWindowChecks
{
    internal static void Run(string parent, Action<string, uint, byte> write)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                string data = Directory.CreateDirectory(Path.Combine(parent, "main-ui-settings")).FullName;
                string source = Directory.CreateDirectory(Path.Combine(parent, "main-ui-source")).FullName;
                string destination = Directory.CreateDirectory(Path.Combine(parent, "main-ui-archive")).FullName;
                WatchFolderSettings.Save(data, []);
                File.WriteAllText(Path.Combine(data, "working_folder.txt"), source);
                write(Path.Combine(source, "startup.avi"), 2, 90);
                new ArchiveSettings { Sources = [source], Destination = destination, WatchContinuously = true,
                    Speed = ArchiveSpeed.Fast, SettlingSeconds = 0 }.Save(data);
                using var form = new Form1(data, registerStartup: false);
                form.Show(); Application.DoEvents();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var worker = (ArchiveWorker)typeof(Form1).GetField("archiveWorker", flags)!.GetValue(form)!;
                var timeout = System.Diagnostics.Stopwatch.StartNew();
                int ticks = 0;
                using var timer = new System.Windows.Forms.Timer { Interval = 20 };
                timer.Tick += (_, _) => ticks++; timer.Start();
                while (worker.Status.Copied != 1)
                {
                    if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("Main window startup watcher timed out.");
                    Application.DoEvents(); Thread.Sleep(5);
                }
                Check(worker.Running && ArchiveSettings.Load(data).WatchContinuously,
                    "Tray startup automatically resumes explicitly enabled watching using separate persisted settings");
                typeof(Form1).GetMethod("ShowWindow", flags)!.Invoke(form, [form, EventArgs.Empty]);
                var buttons = Descendants(form).OfType<Button>().ToList();
                Check(buttons.All(b => b.Text != "Presets") && buttons.Any(b => b.Text == "Seek duplications"),
                    "Main window reclaims the Presets button slot for Seek duplications");
                foreach (var button in buttons.Where(b => b.ContextMenuStrip is not null))
                {
                    var menu = button.ContextMenuStrip!;
                    typeof(ContextMenuStrip).GetMethod("OnOpening", flags)!.Invoke(menu, [new CancelEventArgs()]);
                    Check(menu.Items.OfType<ToolStripMenuItem>().Any(item => item.Text == "Manage presets..."),
                        $"{button.Text}: empty action presets retain Manage presets access");
                }
                var tray = (NotifyIcon)typeof(Form1).GetField("trayIcon", flags)!.GetValue(form)!;
                Check(tray.Visible && tray.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Any(item => item.Text == "Manage presets...")
                    && tray.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Any(item => item.Text == "Seek duplications"),
                    "Tray exposes preset management and two-directory duplicate review");
                var open = (Task)typeof(Form1).GetMethod("RunArchiveAsync", flags)!.Invoke(form, [])!;
                while (!open.IsCompleted) { Application.DoEvents(); Thread.Sleep(5); }
                open.GetAwaiter().GetResult();
                var archive = (ArchiveForm)typeof(Form1).GetField("archiveForm", flags)!.GetValue(form)!;
                archive.Close(); Application.DoEvents();
                Check(worker.Running, "Closing Archive's status window leaves the active watcher running in the tray");
                typeof(Form1).GetMethod("ShowWindow", flags)!.Invoke(form, [form, EventArgs.Empty]);
                Application.DoEvents();
                Check(form.Visible && form.WindowState == FormWindowState.Normal && form.ClientSize.Width >= 500,
                    "Showing the main window restores its full layout after tray startup and Archive review");
                if (Environment.GetEnvironmentVariable("CLIP_MANAGER_UI_QA") is { Length: > 0 } qa)
                {
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(Path.Combine(qa, "main.png"));
                }
                form.Close(); Application.DoEvents();
                Check(!form.Visible && worker.Running, "Closing the main window hides it without stopping continuous watching");
                Thread.Sleep(30); Application.DoEvents();
                Check(ticks > 0, "WinForms messages and timers remain responsive during archive startup and tray operation");
                typeof(Form1).GetMethod("OnFormClosing", flags)!.Invoke(form, [new FormClosingEventArgs(CloseReason.ApplicationExitCall, false)]);
                Check(!worker.Running, "Application Exit stops the watcher safely; repeated disposal is harmless");
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw new Exception("Main window checks failed", error);
    }
    private static IEnumerable<Control> Descendants(Control control)
    { foreach (Control child in control.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
}
