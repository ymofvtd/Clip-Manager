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

            // If no folder is configured, prompt the user to set one
            if (string.IsNullOrEmpty(folderPath))
            {
                using var dlg = new FolderBrowserDialog 
                { 
                    Description = "Select the folder to monitor for video files" 
                };

                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    folderPath = dlg.SelectedPath;
                    SaveFolderPathToConfig(folderPath);
                }
                else
                {
                    // If user cancels, use a temporary folder or show a warning
                    MessageBox.Show(
                        "No folder configured. Please set one using the tray menu.",
                        "Configuration Required",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                    folderPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }
            }

            SetupTrayIcon();
            SetupWatcher();
            SetStartup();

            // Hide the window immediately
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
        }

        private string GetConfigPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VideoTrayApp",
                "config.txt"
            );
        }

        private string LoadFolderPathFromConfig()
        {
            string configPath = GetConfigPath();

            if (System.IO.File.Exists(configPath))
            {
                return System.IO.File.ReadAllText(configPath).Trim();
            }

            return string.Empty; // Return empty if no config exists
        }

        private void SaveFolderPathToConfig(string path)
        {
            string configPath = GetConfigPath();
            string configDir = Path.GetDirectoryName(configPath);

            // Ensure the config directory exists
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }

            System.IO.File.WriteAllText(configPath, path);
        }

        private void SetupTrayIcon()
        {
            trayIcon = new NotifyIcon()
            {
                Icon = LoadApplicationIcon(),
                Visible = true,
                Text = "Video Duration Tracker"
            };

            // Left-click to toggle window visibility
            trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ToggleWindowVisibility();
                }
            };

            // Right-click menu for the icon
            var menu = new ContextMenuStrip();
            menu.Items.Add("Check Now", null, (s, e) => UpdateDuration());
            menu.Items.Add("Set Folder", null, SetFolderPath);
            menu.Items.Add("Show Window", null, ShowWindow);
            menu.Items.Add("Exit", null, (s, e) => Application.Exit());
            trayIcon.ContextMenuStrip = menu;
        }

        private Icon LoadApplicationIcon()
        {
            try
            {
                // Try to load icon from the application directory
                string iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
                if (File.Exists(iconPath))
                {
                    return new Icon(iconPath);
                }

                // Try loading from current directory as fallback
                if (File.Exists("icon.ico"))
                {
                    return new Icon("icon.ico");
                }

                // If icon not found, use system application icon
                return SystemIcons.Application;
            }
            catch
            {
                // If anything fails, use system application icon
                return SystemIcons.Application;
            }
        }

        private void SetupWatcher()
        {
            watcher = new FileSystemWatcher(folderPath);
            // Filter to only watch for video files
            watcher.Filters.Add("*.mp4");
            watcher.Filters.Add("*.mov");
            watcher.Filters.Add("*.m4v");
            watcher.Filters.Add("*.mxf");
            watcher.Filters.Add("*.avi");
            watcher.Filters.Add("*.mkv");
            watcher.Filters.Add("*.wmv");
            watcher.Filters.Add("*.mts");
            watcher.Filters.Add("*.m2ts");
            watcher.Filters.Add("*.mpg");
            watcher.Filters.Add("*.mpeg");
            watcher.Filters.Add("*.3gp");
            watcher.Filters.Add("*.flv");
            watcher.Filters.Add("*.webm");

            watcher.Created += (s, e) => ScheduleUpdate();
            watcher.Deleted += (s, e) => ScheduleUpdate();
            watcher.Renamed += (s, e) => ScheduleUpdate();
            watcher.IncludeSubdirectories = true;
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
                // Ensure the parent folder exists
                if (!Directory.Exists(folderPath)) return;

                var parentDir = new DirectoryInfo(folderPath);
                TimeSpan parentFolderTotal = TimeSpan.Zero;
                TimeSpan firstSubfolderTotal = TimeSpan.Zero;
                string firstSubfolderName = "";

                // Process videos in the parent folder itself
                var parentFiles = parentDir.GetFiles()
                    .Where(f => DefaultVideoExts.Contains(f.Extension))
                    .ToList();

                foreach (var file in parentFiles)
                {
                    try
                    {
                        using var video = TagLib.File.Create(file.FullName);
                        parentFolderTotal += video.Properties.Duration;
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

                // Create duration file for parent folder if it has videos
                if (parentFolderTotal > TimeSpan.Zero)
                {
                    string fileNameOnly = $"{(int)parentFolderTotal.TotalMinutes}M{parentFolderTotal.Seconds}S";
                    string fullFileName = fileNameOnly + ".txt";

                    // Remove any old .txt files in parent folder
                    foreach (var txtFile in parentDir.GetFiles("*.txt"))
                    {
                        try
                        {
                            txtFile.Delete();
                        }
                        catch
                        {
                            // Ignore if file is in use
                        }
                    }

                    // Create the new text file
                    System.IO.File.WriteAllText(Path.Combine(folderPath, fullFileName), "Total Duration Updated");
                }

                // Process each subfolder
                foreach (var subfolder in parentDir.GetDirectories())
                {
                    try
                    {
                        TimeSpan subfolderTotal = TimeSpan.Zero;

                        // Get all video files in this subfolder (not recursive)
                        var files = subfolder.GetFiles()
                            .Where(f => DefaultVideoExts.Contains(f.Extension))
                            .ToList();

                        if (files.Count == 0) continue;

                        foreach (var file in files)
                        {
                            try
                            {
                                using var video = TagLib.File.Create(file.FullName);
                                subfolderTotal += video.Properties.Duration;
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

                        // Create the duration file for this subfolder
                        if (subfolderTotal > TimeSpan.Zero)
                        {
                            string fileNameOnly = $"{(int)subfolderTotal.TotalMinutes}M{subfolderTotal.Seconds}S";
                            string fullFileName = fileNameOnly + ".txt";

                            // Remove any old .txt files in this subfolder
                            foreach (var txtFile in subfolder.GetFiles("*.txt"))
                            {
                                try
                                {
                                    txtFile.Delete();
                                }
                                catch
                                {
                                    // Ignore if file is in use
                                }
                            }

                            // Create the new text file
                            System.IO.File.WriteAllText(Path.Combine(subfolder.FullName, fullFileName), "Total Duration Updated");

                            // Track first subfolder for display
                            if (string.IsNullOrEmpty(firstSubfolderName))
                            {
                                firstSubfolderName = subfolder.Name;
                                firstSubfolderTotal = subfolderTotal;
                            }
                        }
                    }
                    catch
                    {
                        // Skip this subfolder if there's an error
                    }
                }

                // Update the label with priority: parent folder first, then first subfolder
                lblTotalTime.Invoke((MethodInvoker)delegate
                {
                    if (parentFolderTotal > TimeSpan.Zero)
                    {
                        string fileNameOnly = $"{(int)parentFolderTotal.TotalMinutes}M{parentFolderTotal.Seconds}S";
                        lblTotalTime.Text = $"Parent: {fileNameOnly}";
                    }
                    else if (!string.IsNullOrEmpty(firstSubfolderName))
                    {
                        string fileNameOnly = $"{(int)firstSubfolderTotal.TotalMinutes}M{firstSubfolderTotal.Seconds}S";
                        lblTotalTime.Text = $"{firstSubfolderName}: {fileNameOnly}";
                    }
                    else
                    {
                        lblTotalTime.Text = "No videos found";
                    }
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
            UpdateDuration(); // Calculate duration on startup
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
            }

            base.OnFormClosing(e);
        }

        private void ShowWindow(object? sender, EventArgs e)
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.BringToFront();
        }

        private void ToggleWindowVisibility()
        {
            if (this.Visible && this.WindowState == FormWindowState.Normal)
            {
                // Window is visible, so hide it
                this.Hide();
                this.WindowState = FormWindowState.Minimized;
                this.ShowInTaskbar = false;
            }
            else
            {
                // Window is hidden, so show it
                this.Show();
                this.WindowState = FormWindowState.Normal;
                this.ShowInTaskbar = true;
                this.BringToFront();
            }
        }

        private void SetFolderPath(object? sender, EventArgs e)
        {
            using var settingsForm = new SettingsForm(folderPath);

            if (settingsForm.ShowDialog() == DialogResult.OK)
            {
                // Update and save the new folder path
                folderPath = settingsForm.SelectedFolderPath;
                SaveFolderPathToConfig(folderPath);

                // Restart the watcher with the new folder
                watcher?.Dispose();
                SetupWatcher();

                // Trigger an immediate duration check
                UpdateDuration();

                MessageBox.Show(
                    $"Folder updated to:\n{folderPath}",
                    "Folder Updated",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
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
            lblTotalTime.Invoke((MethodInvoker)delegate
            {
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
            lblTotalTime.Invoke((MethodInvoker)delegate
            {
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

        private void btnCheck_Click(object? sender, EventArgs e)
        {
            // Provide quick feedback and trigger an immediate duration update
            lblTotalTime.Invoke((MethodInvoker)delegate
            {
                lblTotalTime.Text = "Checking...";
            });
            UpdateDuration();
        }

        private void btnPrepare_Click(object? sender, EventArgs e)
        {
            // Step 1: Select source folder (where numbered clips are)
            using var sourceDialog = new FolderBrowserDialog 
            { 
                Description = "Select folder containing numbered clips (e.g., 349.mp4)" 
            };
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath)) 
                sourceDialog.SelectedPath = folderPath;
            if (sourceDialog.ShowDialog() != DialogResult.OK) return;

            string sourceFolder = sourceDialog.SelectedPath;

            // Step 2: Select destination folder or create new one
            var destResult = MessageBox.Show(
                "Create a new folder for this batch?\n\nYes = Create new folder\nNo = Select existing folder",
                "Destination Folder",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question
            );

            if (destResult == DialogResult.Cancel) return;

            string destinationFolder;

            if (destResult == DialogResult.Yes)
            {
                // Create new folder with timestamp
                string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string batchFolderName = $"batch_{timestamp}";
                destinationFolder = Path.Combine(sourceFolder, batchFolderName);
                Directory.CreateDirectory(destinationFolder);
            }
            else
            {
                // User selects existing folder
                using var destDialog = new FolderBrowserDialog 
                { 
                    Description = "Select destination folder (where to move numbered clips)" 
                };
                if (destDialog.ShowDialog() != DialogResult.OK) return;
                destinationFolder = destDialog.SelectedPath;
            }

            // Step 3: Ask for desired batch duration
            string durationInput = Interaction.InputBox(
                "Desired batch duration in minutes (default 20):",
                "Batch Duration",
                "20"
            );

            if (!int.TryParse(durationInput, out int durationMinutes) || durationMinutes <= 0)
            {
                durationMinutes = 20; // Default to 20 minutes
            }

            TimeSpan batchDurationLimit = TimeSpan.FromMinutes(durationMinutes);

            // Show calculating indicator
            lblTotalTime.Invoke((MethodInvoker)delegate
            {
                lblTotalTime.Text = "Calculating...";
            });

            try
            {
                BatchNumberedVideos(sourceFolder, destinationFolder, batchDurationLimit);
                MessageBox.Show("Batch preparation completed.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        //
        // Implementation of BatchNumberedVideos - moves numbered .mp4 files from source to destination,
        // keeping total duration under the specified limit. If moving a file exceeds the limit,
        // it stops and leaves that file in the source folder.
        //
        private void BatchNumberedVideos(string sourceFolder, string destinationFolder, TimeSpan durationLimit)
        {
            var srcDir = new DirectoryInfo(sourceFolder);
            if (!srcDir.Exists) throw new DirectoryNotFoundException($"Source folder not found: {sourceFolder}");

            var destDir = new DirectoryInfo(destinationFolder);
            if (!destDir.Exists) throw new DirectoryNotFoundException($"Destination folder not found: {destinationFolder}");

            // Filter for .mp4 files in source where filename is strictly a number (e.g., "349.mp4")
            var files = srcDir.EnumerateFiles("*.mp4", SearchOption.TopDirectoryOnly)
                .Where(fi => IsStrictlyNumeric(Path.GetFileNameWithoutExtension(fi.Name)))
                .OrderBy(fi =>
                {
                    if (int.TryParse(Path.GetFileNameWithoutExtension(fi.Name), out int num))
                        return num;
                    return int.MaxValue;
                })
                .ToList();

            if (files.Count == 0)
            {
                lblTotalTime.Invoke((MethodInvoker)delegate
                {
                    lblTotalTime.Text = "No numbered videos found in source";
                });
                return;
            }

            // Calculate existing duration in destination folder (all video files)
            TimeSpan existingDuration = TimeSpan.Zero;
            var destFiles = destDir.EnumerateFiles()
                .Where(fi => DefaultVideoExts.Contains(fi.Extension))
                .ToList();

            foreach (var file in destFiles)
            {
                try
                {
                    using var video = TagLib.File.Create(file.FullName);
                    existingDuration += video.Properties.Duration;
                }
                catch (IOException)
                {
                    // File is being written - skip for now
                    continue;
                }
                catch
                {
                    // Skip files that can't be read
                    continue;
                }
            }

            // Use the provided duration limit
            TimeSpan totalDuration = existingDuration;
            int movedCount = 0;

            foreach (var file in files)
            {
                try
                {
                    // Get video duration using TagLib
                    TimeSpan videoDuration;
                    try
                    {
                        using var video = TagLib.File.Create(file.FullName);
                        videoDuration = video.Properties.Duration;
                    }
                    catch (IOException)
                    {
                        // File is being written - skip for now
                        continue;
                    }
                    catch
                    {
                        // Skip files that can't be read
                        continue;
                    }

                    // Check if we've already reached the limit
                    if (totalDuration >= durationLimit)
                    {
                        // Already at or exceeded the limit - stop here
                        break;
                    }

                    // Add this file to reach the limit
                    TimeSpan projectedTotal = totalDuration + videoDuration;
                    string targetPath = Path.Combine(destinationFolder, file.Name);
                    file.MoveTo(targetPath);
                    totalDuration = projectedTotal;
                    movedCount++;
                }
                catch (Exception ex)
                {
                    // Log error but continue with next file
                    System.Diagnostics.Debug.WriteLine($"Error processing {file.Name}: {ex.Message}");
                    continue;
                }
            }

            // Update label with result
            lblTotalTime.Invoke((MethodInvoker)delegate
            {
                int minutes = (int)totalDuration.TotalMinutes;
                int seconds = totalDuration.Seconds;
                lblTotalTime.Text = $"Batched: {movedCount} files ({minutes}m{seconds}s)";
            });
        }

        //
        // Helper method to check if filename is strictly numeric
        //
        private static bool IsStrictlyNumeric(string text)
        {
            return !string.IsNullOrEmpty(text) && text.All(char.IsDigit);
        }

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

        private void folderBrowserDialog1_HelpRequest(object sender, EventArgs e)
        {

        }
    }
}