using Microsoft.Win32;
using System.IO;
using System.Reflection.Emit;
using System.Diagnostics;
using Microsoft.VisualBasic; // for InputBox
using System.Text.RegularExpressions;
using System.Linq;
using System.Text;

namespace VideoTrayApp
{
    public partial class Form1 : Form
    {
        private NotifyIcon trayIcon;
        private FileSystemWatcher watcher;
        private string folderPath;
        private System.Timers.Timer debounceTimer;
        private bool isDirty = false;

        // Common video extensions used by original Python scripts
        private static readonly HashSet<string> DefaultVideoExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov", ".m4v", ".mxf", ".avi", ".mkv", ".wmv",
            ".mts", ".m2ts", ".mpg", ".mpeg", ".3gp", ".flv", ".webm"
        };

        public Form1()
        {
            InitializeComponent();
            folderPath = LoadFolderPathFromConfig();
            SetupTrayIcon();
            SetupWatcher();
            SetStartup();

            // Hide the window immediately
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
        }

        private string LoadFolderPathFromConfig()
        {
            string configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VideoTrayApp",
                "config.txt"
            );

            if (System.IO.File.Exists(configPath))
            {
                return System.IO.File.ReadAllText(configPath).Trim();
            }

            return @"F:\EXPORT\clips\current"; // Fallback
        }

        private void SetupTrayIcon()
        {
            trayIcon = new NotifyIcon()
            {
                Icon = SystemIcons.Application, // You can use a custom .ico here!
                Visible = true,
                Text = "Video Duration Tracker"
            };

            // Right-click menu for the icon
            var menu = new ContextMenuStrip();
            menu.Items.Add("Check Now", null, (s, e) => UpdateDuration());
            menu.Items.Add("Show Window", null, ShowWindow);
            menu.Items.Add("Exit", null, (s, e) => Application.Exit());
            trayIcon.ContextMenuStrip = menu;
        }

        private void SetupWatcher()
        {
            watcher = new FileSystemWatcher(folderPath, "*.mp4");
            watcher.Created += (s, e) => ScheduleUpdate();
            watcher.Deleted += (s, e) => ScheduleUpdate();
            watcher.EnableRaisingEvents = true;

            debounceTimer = new System.Timers.Timer(500);
            debounceTimer.Elapsed += (s, e) =>
            {
                if (isDirty)
                {
                    isDirty = false;
                    UpdateDuration();
                }
            };
            debounceTimer.AutoReset = false;

            UpdateDuration(); // Run once at start
        }

        private void ScheduleUpdate()
        {
            isDirty = true;
            debounceTimer.Stop();
            debounceTimer.Start();
        }

        private void UpdateDuration()
        {
            try
            {
                TimeSpan total = TimeSpan.Zero;

                // Ensure the folder exists before looking inside
                if (!Directory.Exists(folderPath)) return;

                var files = Directory.GetFiles(folderPath, "*.mp4");

                foreach (var file in files)
                {
                    try
                    {
                        using var video = TagLib.File.Create(file);
                        total += video.Properties.Duration;
                    }
                    catch (IOException)
                    {
                        // File is being written - skip for now
                        continue;
                    }
                    catch
                    {
                        // Skip other errors
                    }
                }

                // Create the 20M23S style name
                string fileNameOnly = $"{(int)total.TotalMinutes}M{total.Seconds}S";
                string fullFileName = fileNameOnly + ".txt";

                // Remove any old .txt files first
                foreach (var txtFile in Directory.GetFiles(folderPath, "*.txt"))
                {
                    try
                    {
                        System.IO.File.Delete(txtFile);
                    }
                    catch
                    {
                        // Ignore if file is in use
                    }
                }

                // Create the new text file
                System.IO.File.WriteAllText(Path.Combine(folderPath, fullFileName), "Total Duration Updated");

                // Update the label safely
                lblTotalTime.Invoke((MethodInvoker)delegate {
                    lblTotalTime.Text = $"Total Time: {fileNameOnly}";
                });
            }
            catch (Exception ex)
            {
                // Log or silently handle
            }
        }

        private void SetStartup()
        {
            // This adds your app to the Windows Registry to run on boot
            RegistryKey rk = Registry.CurrentUser.OpenSubKey
                ("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            rk.SetValue("VideoTrayApp", Application.ExecutablePath);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            this.Hide(); // Ensure it stays hidden
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                this.WindowState = FormWindowState.Minimized;
                this.ShowInTaskbar = false;
            }
            else
            {
                watcher?.Dispose();
                debounceTimer?.Dispose();
                trayIcon?.Dispose();
                base.OnFormClosing(e);
            }
        }

        private void ShowWindow(object? sender, EventArgs e)
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.BringToFront();
        }

        private void label1_Click(object sender, EventArgs e)
        {

        }

        // Button handlers now call the internal C# implementations
        private void btnShuffleAndName_Click(object? sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog { Description = "Select folder to process" };
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath)) dlg.SelectedPath = folderPath;
            if (dlg.ShowDialog() != DialogResult.OK) return;

            string targetFolder = dlg.SelectedPath;
            string startInput = Interaction.InputBox("Starting number (leave blank for 0):", "Start Number", "0");
            if (!int.TryParse(string.IsNullOrWhiteSpace(startInput) ? "0" : startInput.Trim(), out int start)) start = 0;

            string padInput = Interaction.InputBox("Padding width (0 = none):", "Padding", "0");
            if (!int.TryParse(padInput.Trim(), out int pad)) pad = 0;
            string prefix = Interaction.InputBox("Optional filename prefix:", "Prefix", "");

            // Show calculating indicator
            lblTotalTime.Invoke((MethodInvoker)delegate {
                lblTotalTime.Text = "Calculating...";
            });

            try
            {
                ShuffleAndNameStepTen(targetFolder, start, pad, prefix);
                MessageBox.Show("Shuffle & Name completed.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdateDuration(); // Refresh the display
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Operation failed:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateDuration(); // Reset display on error
            }
        }

        private void btnNameTenSteps_Click(object? sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog { Description = "Select folder to process" };
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath)) dlg.SelectedPath = folderPath;
            if (dlg.ShowDialog() != DialogResult.OK) return;

            string targetFolder = dlg.SelectedPath;
            string startInput = Interaction.InputBox("Starting number (leave blank for 0):", "Start Number", "0");
            if (!int.TryParse(string.IsNullOrWhiteSpace(startInput) ? "0" : startInput.Trim(), out int start)) start = 0;

            // Show calculating indicator
            lblTotalTime.Invoke((MethodInvoker)delegate {
                lblTotalTime.Text = "Calculating...";
            });

            try
            {
                NameTenSteps(targetFolder, start);
                MessageBox.Show("Name by 10s completed.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdateDuration(); // Refresh the display
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Operation failed:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateDuration(); // Reset display on error
            }
        }

        //
        // Implementation of NameTenSteps (two-pass rename to clear namespace and then 0,10,20...)
        //
        private void NameTenSteps(string folder, int start)
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists) throw new DirectoryNotFoundException(folder);

            // Gather files filtered by known video extensions
            var files = dir.EnumerateFiles()
                .Where(fi => !fi.Name.StartsWith(".") && DefaultVideoExts.Contains(fi.Extension))
                .ToList();

            if (files.Count == 0) return;

            // Natural sort similar to Python version
            files.Sort((a, b) => NaturalSortCompare(a.Name, b.Name));

            // Pass 1: rename to TEMP_<random>
            var rng = new Random();
            var tempMap = new List<(string OriginalName, FileInfo TempFile)>();
            foreach (var fi in files)
            {
                var tempName = $"TEMP_{GenId(rng, 12)}{fi.Extension}";
                var tempPath = Path.Combine(folder, tempName);
                tempPath = EnsureUniquePath(tempPath);
                File.Move(fi.FullName, tempPath);
                tempMap.Add((fi.Name, new FileInfo(tempPath)));
            }

            // Pass 2: final naming by tens
            int seq = start;
            foreach (var (originalName, currentFile) in tempMap)
            {
                var finalName = $"{seq}{currentFile.Extension}";
                var finalPath = Path.Combine(folder, finalName);
                finalPath = EnsureUniquePath(finalPath);
                File.Move(currentFile.FullName, finalPath);
                seq += 10;
            }
        }

        //
        // Implementation of ShuffleAndNameStepTen (two-pass: randomize ids then rename by tens)
        //
        private void ShuffleAndNameStepTen(string folder, int start, int pad, string prefix)
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists) throw new DirectoryNotFoundException(folder);

            // Gather files matching default video extensions
            var files = dir.EnumerateFiles()
                .Where(fi => !fi.Name.StartsWith(".") && DefaultVideoExts.Contains(fi.Extension))
                .OrderBy(fi => fi.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0) return;

            var rng = new Random();
            var usedIds = new HashSet<string>();
            var stage1Map = new List<(string Original, string RandomName)>(); // store names

            // Stage 1: assign unique random ids and rename files
            foreach (var fi in files)
            {
                string rid;
                do
                {
                    rid = GenId(rng, 10);
                } while (!usedIds.Add(rid));

                var newName = rid + fi.Extension;
                var newPath = Path.Combine(folder, newName);
                newPath = EnsureUniquePath(newPath);
                File.Move(fi.FullName, newPath);
                stage1Map.Add((fi.Name, Path.GetFileName(newPath)));
            }

            // After renaming, collect produced random files and sort by name
            var producedNames = new HashSet<string>(stage1Map.Select(t => t.RandomName));
            var randomizedFiles = dir.EnumerateFiles()
                .Where(f => producedNames.Contains(f.Name))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Prepare CSV log
            string now = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string logPath = Path.Combine(folder, $"rename_mapping_{now}.csv");
            var sb = new StringBuilder();
            sb.AppendLine("original,random,final");

            // Stage 2: rename with step 10
            int seq = start;
            foreach (var current in randomizedFiles)
            {
                string numStr = pad > 0 ? seq.ToString().PadLeft(pad, '0') : seq.ToString();
                var finalName = $"{prefix}{numStr}{current.Extension}";
                var finalPath = Path.Combine(folder, finalName);
                finalPath = EnsureUniquePath(finalPath);
                // Append to CSV
                var original = stage1Map.FirstOrDefault(t => t.RandomName == current.Name).Original ?? "";
                sb.AppendLine($"{EscapeCsv(original)},{EscapeCsv(current.Name)},{EscapeCsv(Path.GetFileName(finalPath))}");
                File.Move(current.FullName, finalPath);
                seq += 10;
            }

            File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
        }

        // Small helpers

        private static string EscapeCsv(string s)
        {
            if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            {
                return $"\"{s.Replace("\"", "\"\"")}\"";
            }
            return s;
        }

        private static string GenId(Random rng, int length)
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
                sb.Append(alphabet[rng.Next(alphabet.Length)]);
            return sb.ToString();
        }

        private static string EnsureUniquePath(string targetPath)
        {
            if (!File.Exists(targetPath) && !Directory.Exists(targetPath)) return targetPath;
            string dir = Path.GetDirectoryName(targetPath) ?? "";
            string name = Path.GetFileNameWithoutExtension(targetPath);
            string ext = Path.GetExtension(targetPath);
            int i = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{name}__{i}{ext}");
                i++;
            } while (File.Exists(candidate) || Directory.Exists(candidate));
            return candidate;
        }

        // Natural sort helper adapted from Python version
        private static int NaturalSortCompare(string a, string b)
        {
            var ax = Regex.Split(a.Replace(" ", ""), "([0-9]+)").Where(s => s != "").ToArray();
            var bx = Regex.Split(b.Replace(" ", ""), "([0-9]+)").Where(s => s != "").ToArray();
            int n = Math.Min(ax.Length, bx.Length);
            for (int i = 0; i < n; i++)
            {
                if (int.TryParse(ax[i], out int ai) && int.TryParse(bx[i], out int bi))
                {
                    int c = ai.CompareTo(bi);
                    if (c != 0) return c;
                }
                else
                {
                    int c = string.Compare(ax[i], bx[i], StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return c;
                }
            }
            return ax.Length.CompareTo(bx.Length);
        }

        private bool StartProcessIfAvailable(string exe, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}