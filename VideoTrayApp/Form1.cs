using Microsoft.Win32;
using System.IO;
using System.Diagnostics;
using Microsoft.VisualBasic; // for InputBox
using System.Text.RegularExpressions;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
            menu.Items.Add("Check Now", null, async (s, e) => await RunCheckWithProgressAsync());
            menu.Items.Add("Archive", null, async (s, e) => await RunArchiveAsync());
            menu.Items.Add("Set Folder", null, SetFolderPath);
            menu.Items.Add("Show Window", null, ShowWindow);
            menu.Items.Add("Check for Updates", null, async (s, e) => await RunCheckForUpdateAsync());
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
                UpdateDurationCore(null, CancellationToken.None);
            }
            catch
            {
                // Log or silently handle
            }
        }

        private void UpdateDurationCore(ProgressForm? progress, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(folderPath)) return;

            var parentDir = new DirectoryInfo(folderPath);
            TimeSpan parentFolderTotal = TimeSpan.Zero;
            TimeSpan firstSubfolderTotal = TimeSpan.Zero;
            string firstSubfolderName = "";

            var parentFiles = parentDir.GetFiles()
                .Where(f => DefaultVideoExts.Contains(f.Extension))
                .ToList();

            var subfolders = parentDir.GetDirectories().ToList();
            int totalFiles = parentFiles.Count + subfolders.Sum(sf =>
            {
                try
                {
                    return sf.GetFiles().Count(f => DefaultVideoExts.Contains(f.Extension));
                }
                catch
                {
                    return 0;
                }
            });

            int processed = 0;
            progress?.Report(0, totalFiles, "Checking videos...");

            foreach (var file in parentFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress?.Report(processed, totalFiles, $"Reading {file.Name}...");

                try
                {
                    using var video = TagLib.File.Create(file.FullName);
                    parentFolderTotal += video.Properties.Duration;
                }
                catch (IOException)
                {
                    continue;
                }
                catch
                {
                    // Skip other errors
                }
            }

            if (parentFolderTotal > TimeSpan.Zero)
            {
                string fileNameOnly = $"{(int)parentFolderTotal.TotalMinutes}M{parentFolderTotal.Seconds}S";
                string fullFileName = fileNameOnly + ".txt";

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

                System.IO.File.WriteAllText(Path.Combine(folderPath, fullFileName), "Total Duration Updated");
            }

            foreach (var subfolder in subfolders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    TimeSpan subfolderTotal = TimeSpan.Zero;
                    var files = subfolder.GetFiles()
                        .Where(f => DefaultVideoExts.Contains(f.Extension))
                        .ToList();

                    if (files.Count == 0) continue;

                    foreach (var file in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        processed++;
                        progress?.Report(processed, totalFiles, $"Reading {subfolder.Name}\\{file.Name}...");

                        try
                        {
                            using var video = TagLib.File.Create(file.FullName);
                            subfolderTotal += video.Properties.Duration;
                        }
                        catch (IOException)
                        {
                            continue;
                        }
                        catch
                        {
                            // Skip other errors
                        }
                    }

                    if (subfolderTotal > TimeSpan.Zero)
                    {
                        string fileNameOnly = $"{(int)subfolderTotal.TotalMinutes}M{subfolderTotal.Seconds}S";
                        string fullFileName = fileNameOnly + ".txt";

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

                        System.IO.File.WriteAllText(Path.Combine(subfolder.FullName, fullFileName), "Total Duration Updated");

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

        private void SetButtonsEnabled(bool enabled)
        {
            if (InvokeRequired)
            {
                Invoke(SetButtonsEnabled, enabled);
                return;
            }

            btnShuffleAndName.Enabled = enabled;
            btnNameTenSteps.Enabled = enabled;
            btnCheck.Enabled = enabled;
            btnPrepare.Enabled = enabled;
            btnShuffleRandom.Enabled = enabled;
            btnArchive.Enabled = enabled;
        }

        private async Task RunWithProgressAsync(
            string title,
            Func<ProgressForm, CancellationToken, Task<string?>> work,
            bool refreshDurationOnComplete = true,
            bool showSuccessMessage = false,
            string? successMessage = null)
        {
            SetButtonsEnabled(false);

            using var progressForm = new ProgressForm();
            progressForm.BeginOperation(title);
            progressForm.Show(this);

            var workTask = Task.Run(() => work(progressForm, progressForm.CancellationToken));
            try
            {
                string? resultMessage = await workTask;
                progressForm.Complete("Done");

                if (showSuccessMessage)
                {
                    MessageBox.Show(
                        resultMessage ?? successMessage ?? "Operation completed.",
                        "Done",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show("Operation cancelled.", "Cancelled", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Operation failed:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (progressForm.Visible)
                {
                    progressForm.Close();
                }

                SetButtonsEnabled(true);

                if (refreshDurationOnComplete)
                {
                    try
                    {
                        UpdateDuration();
                    }
                    catch
                    {
                        // Ignore refresh errors
                    }
                }
            }
        }

        private Task RunCheckWithProgressAsync()
        {
            return RunWithProgressAsync(
                "Check Duration",
                (progress, ct) =>
                {
                    UpdateDurationCore(progress, ct);
                    return Task.FromResult<string?>(null);
                },
                refreshDurationOnComplete: false);
        }

        private async Task RunCheckForUpdateAsync()
        {
            SetButtonsEnabled(false);

            try
            {
                using var checkForm = new ProgressForm();
                checkForm.BeginOperation("Check for Updates");
                checkForm.Show(this);
                checkForm.SetIndeterminate("Checking for updates...");

                UpdateCheckResult result;
                try
                {
                    result = await AppUpdater.CheckForUpdateAsync(checkForm.CancellationToken);
                }
                finally
                {
                    if (checkForm.Visible)
                        checkForm.Close();
                }

                if (!result.UpdateAvailable)
                {
                    MessageBox.Show(
                        $"You already have the latest version (v{FormatVersion(result.CurrentVersion)}).",
                        "No Updates",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                var answer = MessageBox.Show(
                    $"A new version is available: v{FormatVersion(result.LatestVersion!)}\n\n" +
                    $"You are running v{FormatVersion(result.CurrentVersion)}.\n\n" +
                    "Would you like to download and install it now?",
                    "Update Available",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (answer != DialogResult.Yes)
                    return;

                await RunWithProgressAsync(
                    "Downloading Update",
                    async (progress, ct) =>
                    {
                        string downloadedPath = await AppUpdater.DownloadUpdateAsync(
                            result.DownloadUrl!,
                            result.LatestVersion!,
                            (downloaded, total) =>
                            {
                                if (total is > 0)
                                {
                                    int percent = (int)(downloaded * 100 / total.Value);
                                    progress.Report((int)downloaded, (int)total.Value, $"Downloading... {percent}%");
                                }
                                else
                                {
                                    progress.SetIndeterminate($"Downloading... {downloaded / 1024 / 1024} MB");
                                }
                            },
                            ct);

                        progress.SetIndeterminate("Applying update...");
                        AppUpdater.ApplyUpdate(downloadedPath);
                        return "The app will now restart to finish updating.";
                    },
                    refreshDurationOnComplete: false,
                    showSuccessMessage: true);

                Application.Exit();
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show("Update cancelled.", "Cancelled", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update failed:\n{ex.Message}", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        private static string FormatVersion(Version version)
        {
            int build = version.Build < 0 ? 0 : version.Build;
            return $"{version.Major}.{version.Minor}.{build}";
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
        private async void btnShuffleAndName_Click(object? sender, EventArgs e)
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

            await RunWithProgressAsync(
                "Shuffle & Name",
                (progress, ct) =>
                {
                    ShuffleAndNameStepTen(targetFolder, start, pad, prefix, progress, ct);
                    return Task.FromResult<string?>(null);
                },
                showSuccessMessage: true,
                successMessage: "Shuffle & Name completed.");
        }

        private async void btnNameTenSteps_Click(object? sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog { Description = "Select folder to process" };
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath)) dlg.SelectedPath = folderPath;
            if (dlg.ShowDialog() != DialogResult.OK) return;

            string targetFolder = dlg.SelectedPath;
            string startInput = Interaction.InputBox("Starting number (leave blank for 0):", "Start Number", "0");
            if (!int.TryParse(string.IsNullOrWhiteSpace(startInput) ? "0" : startInput.Trim(), out int start)) start = 0;

            await RunWithProgressAsync(
                "Name by 10s",
                (progress, ct) =>
                {
                    NameTenSteps(targetFolder, start, progress, ct);
                    return Task.FromResult<string?>(null);
                },
                showSuccessMessage: true,
                successMessage: "Name by 10s completed.");
        }

        private async void btnCheck_Click(object? sender, EventArgs e)
        {
            await RunCheckWithProgressAsync();
        }

        private async void btnPrepare_Click(object? sender, EventArgs e)
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

            await RunWithProgressAsync(
                "Prepare Batch",
                (progress, ct) =>
                {
                    BatchNumberedVideos(sourceFolder, destinationFolder, batchDurationLimit, progress, ct);
                    return Task.FromResult<string?>(null);
                },
                showSuccessMessage: true,
                successMessage: "Batch preparation completed.");
        }

        private async void btnShuffleRandom_Click(object? sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog { Description = "Select folder to shuffle videos to random character names" };
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath)) dlg.SelectedPath = folderPath;
            if (dlg.ShowDialog() != DialogResult.OK) return;

            string targetFolder = dlg.SelectedPath;

            string lenInput = Interaction.InputBox("Random name length (default 12):", "Length", "12");
            if (!int.TryParse(lenInput.Trim(), out int len) || len < 4) len = 12;
            if (len > 64) len = 64;

            await RunWithProgressAsync(
                "Shuffle Random",
                (progress, ct) =>
                {
                    ShuffleToRandom(targetFolder, len, progress, ct);
                    return Task.FromResult<string?>(null);
                },
                showSuccessMessage: true,
                successMessage: "Shuffle to random names completed.");
        }

        private async void btnArchive_Click(object? sender, EventArgs e)
        {
            await RunArchiveAsync();
        }

        private async Task RunArchiveAsync()
        {
            using var dlg = new FolderBrowserDialog { Description = "Select folder to archive .mp4 files from" };
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath)) dlg.SelectedPath = folderPath;
            if (dlg.ShowDialog() != DialogResult.OK) return;

            string sourceFolder = dlg.SelectedPath;

            await RunWithProgressAsync(
                "Archive Videos",
                (progress, ct) => Task.FromResult<string?>(ArchiveVideos(sourceFolder, progress, ct)),
                showSuccessMessage: true);
        }

        //
        // Implementation of NameTenSteps (two-pass rename to clear namespace and then 0,10,20...)
        //
        private void NameTenSteps(string folder, int start, ProgressForm? progress = null, CancellationToken cancellationToken = default)
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists) throw new DirectoryNotFoundException(folder);

            var files = dir.EnumerateFiles()
                .Where(fi => !fi.Name.StartsWith(".") && DefaultVideoExts.Contains(fi.Extension))
                .ToList();

            if (files.Count == 0) return;

            files.Sort((a, b) => NaturalSortCompare(a.Name, b.Name));

            var rng = new Random();
            var tempMap = new List<(string OriginalName, FileInfo TempFile)>();
            int processed = 0;
            int total = files.Count * 2;
            progress?.Report(0, total, "Renaming videos (pass 1)...");

            foreach (var fi in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress?.Report(processed, total, $"Pass 1: {fi.Name}");

                var tempName = $"TEMP_{GenId(rng, 12)}{fi.Extension}";
                var tempPath = Path.Combine(folder, tempName);
                tempPath = EnsureUniquePath(tempPath);
                File.Move(fi.FullName, tempPath);
                tempMap.Add((fi.Name, new FileInfo(tempPath)));
            }

            int seq = start;
            progress?.Report(processed, total, "Renaming videos (pass 2)...");
            foreach (var (originalName, currentFile) in tempMap)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress?.Report(processed, total, $"Pass 2: {currentFile.Name}");

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
        private void ShuffleAndNameStepTen(string folder, int start, int pad, string prefix, ProgressForm? progress = null, CancellationToken cancellationToken = default)
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists) throw new DirectoryNotFoundException(folder);

            var files = dir.EnumerateFiles()
                .Where(fi => !fi.Name.StartsWith(".") && DefaultVideoExts.Contains(fi.Extension))
                .OrderBy(fi => fi.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0) return;

            var rng = new Random();
            var usedIds = new HashSet<string>();
            var stage1Map = new List<(string Original, string RandomName)>();
            int processed = 0;
            int total = files.Count * 2;
            progress?.Report(0, total, "Shuffling videos (stage 1)...");

            foreach (var fi in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress?.Report(processed, total, $"Stage 1: {fi.Name}");

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

            var producedNames = new HashSet<string>(stage1Map.Select(t => t.RandomName));
            var randomizedFiles = dir.EnumerateFiles()
                .Where(f => producedNames.Contains(f.Name))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string now = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string logPath = Path.Combine(folder, $"rename_mapping_{now}.csv");
            var sb = new StringBuilder();
            sb.AppendLine("original,random,final");

            int seq = start;
            progress?.Report(processed, total, "Naming videos (stage 2)...");
            foreach (var current in randomizedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress?.Report(processed, total, $"Stage 2: {current.Name}");

                string numStr = pad > 0 ? seq.ToString().PadLeft(pad, '0') : seq.ToString();
                var finalName = $"{prefix}{numStr}{current.Extension}";
                var finalPath = Path.Combine(folder, finalName);
                finalPath = EnsureUniquePath(finalPath);
                var original = stage1Map.FirstOrDefault(t => t.RandomName == current.Name).Original ?? "";
                sb.AppendLine($"{EscapeCsv(original)},{EscapeCsv(current.Name)},{EscapeCsv(Path.GetFileName(finalPath))}");
                File.Move(current.FullName, finalPath);
                seq += 10;
            }

            File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
        }

        //
        // Implementation of ShuffleToRandom: renames all videos in folder to random character names
        //
        private void ShuffleToRandom(string folder, int length, ProgressForm? progress = null, CancellationToken cancellationToken = default)
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists) throw new DirectoryNotFoundException(folder);

            var files = dir.EnumerateFiles()
                .Where(fi => !fi.Name.StartsWith(".") && DefaultVideoExts.Contains(fi.Extension))
                .OrderBy(fi => fi.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0) return;

            var rng = new Random();
            var usedIds = new HashSet<string>();
            string now = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string logPath = Path.Combine(folder, $"random_shuffle_{now}.csv");
            var sb = new StringBuilder();
            sb.AppendLine("original,random");

            int processed = 0;
            progress?.Report(0, files.Count, "Shuffling videos...");

            foreach (var fi in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress?.Report(processed, files.Count, $"Shuffling {fi.Name}...");

                string rid;
                string candidatePath;
                do
                {
                    rid = GenId(rng, length);
                    string candidateName = rid + fi.Extension;
                    candidatePath = Path.Combine(folder, candidateName);
                } while (usedIds.Contains(rid) || File.Exists(candidatePath) || Directory.Exists(candidatePath));

                usedIds.Add(rid);
                File.Move(fi.FullName, candidatePath);
                sb.AppendLine($"{EscapeCsv(fi.Name)},{EscapeCsv(Path.GetFileName(candidatePath))}");
            }

            File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
        }

        //
        // Implementation of BatchNumberedVideos - moves numbered .mp4 files from source to destination,
        // keeping total duration under the specified limit. If moving a file exceeds the limit,
        // it stops and leaves that file in the source folder.
        //
        private void BatchNumberedVideos(string sourceFolder, string destinationFolder, TimeSpan durationLimit, ProgressForm? progress = null, CancellationToken cancellationToken = default)
        {
            var srcDir = new DirectoryInfo(sourceFolder);
            if (!srcDir.Exists) throw new DirectoryNotFoundException($"Source folder not found: {sourceFolder}");

            var destDir = new DirectoryInfo(destinationFolder);
            if (!destDir.Exists) throw new DirectoryNotFoundException($"Destination folder not found: {destinationFolder}");

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

            TimeSpan existingDuration = TimeSpan.Zero;
            var destFiles = destDir.EnumerateFiles()
                .Where(fi => DefaultVideoExts.Contains(fi.Extension))
                .ToList();

            int totalSteps = destFiles.Count + files.Count;
            int processed = 0;
            progress?.Report(0, totalSteps, "Scanning destination folder...");

            foreach (var file in destFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress?.Report(processed, totalSteps, $"Reading destination: {file.Name}");

                try
                {
                    using var video = TagLib.File.Create(file.FullName);
                    existingDuration += video.Properties.Duration;
                }
                catch (IOException)
                {
                    continue;
                }
                catch
                {
                    continue;
                }
            }

            TimeSpan totalDuration = existingDuration;
            int movedCount = 0;
            progress?.Report(processed, totalSteps, "Moving numbered videos...");

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress?.Report(processed, totalSteps, $"Processing {file.Name}...");

                try
                {
                    TimeSpan videoDuration;
                    try
                    {
                        using var video = TagLib.File.Create(file.FullName);
                        videoDuration = video.Properties.Duration;
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch
                    {
                        continue;
                    }

                    if (totalDuration >= durationLimit)
                    {
                        break;
                    }

                    TimeSpan projectedTotal = totalDuration + videoDuration;
                    string targetPath = Path.Combine(destinationFolder, file.Name);
                    file.MoveTo(targetPath);
                    totalDuration = projectedTotal;
                    movedCount++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error processing {file.Name}: {ex.Message}");
                    continue;
                }
            }

            lblTotalTime.Invoke((MethodInvoker)delegate
            {
                int minutes = (int)totalDuration.TotalMinutes;
                int seconds = totalDuration.Seconds;
                lblTotalTime.Text = $"Batched: {movedCount} files ({minutes}m{seconds}s)";
            });
        }

        private static readonly TimeSpan MaxArchiveDuration = TimeSpan.FromSeconds(90);

        private string ArchiveVideos(string sourceFolder, ProgressForm progress, CancellationToken cancellationToken)
        {
            var sourceDir = new DirectoryInfo(sourceFolder);
            if (!sourceDir.Exists)
                throw new DirectoryNotFoundException($"Source folder not found: {sourceFolder}");

            string archiveFolder = Path.Combine(sourceFolder, "Archive");
            Directory.CreateDirectory(archiveFolder);

            var knownFingerprints = new HashSet<VideoFingerprint>(VideoFingerprintComparer.Instance);
            var archiveFiles = Directory.EnumerateFiles(archiveFolder, "*.mp4", SearchOption.AllDirectories).ToList();

            progress.Report(0, 0, "Indexing existing archive...");
            int archiveIndexed = 0;
            foreach (var archivePath in archiveFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                archiveIndexed++;
                progress.Report(archiveIndexed, archiveFiles.Count, $"Indexing archive: {Path.GetFileName(archivePath)}");

                if (VideoFingerprintFactory.TryCreate(archivePath, out var fingerprint, out _))
                {
                    knownFingerprints.Add(fingerprint!);
                }
            }

            progress.SetIndeterminate("Scanning for .mp4 files...");
            var sourceFiles = Directory
                .EnumerateFiles(sourceFolder, "*.mp4", SearchOption.AllDirectories)
                .Where(path => !IsUnderDirectory(path, archiveFolder))
                .ToList();

            int copied = 0;
            int skippedDuplicates = 0;
            int skippedTooLong = 0;
            int errors = 0;
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string logPath = Path.Combine(archiveFolder, $"archive_log_{timestamp}.csv");
            var log = new StringBuilder();
            log.AppendLine("source_path,action,size_bytes,duration_seconds,destination_path,error");

            for (int i = 0; i < sourceFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sourcePath = sourceFiles[i];
                string fileName = Path.GetFileName(sourcePath);
                progress.Report(i + 1, sourceFiles.Count, $"Checking {fileName}...");

                if (!VideoFingerprintFactory.TryCreate(sourcePath, out var fingerprint, out var error)
                    || fingerprint is null)
                {
                    errors++;
                    log.AppendLine($"{EscapeCsv(sourcePath)},error,,,,,{EscapeCsv(error ?? "Unknown error")}");
                    continue;
                }

                if (!knownFingerprints.Add(fingerprint))
                {
                    skippedDuplicates++;
                    log.AppendLine($"{EscapeCsv(sourcePath)},skipped_duplicate,{fingerprint.Size},{fingerprint.Duration.TotalSeconds:0.###},,");
                    continue;
                }

                if (fingerprint.Duration > MaxArchiveDuration)
                {
                    skippedTooLong++;
                    log.AppendLine($"{EscapeCsv(sourcePath)},skipped_too_long,{fingerprint.Size},{fingerprint.Duration.TotalSeconds:0.###},,");
                    continue;
                }

                try
                {
                    string destinationPath = EnsureUniquePath(Path.Combine(archiveFolder, fileName));
                    progress.Report(i + 1, sourceFiles.Count, $"Copying {fileName}...");
                    File.Copy(sourcePath, destinationPath, overwrite: false);
                    copied++;
                    log.AppendLine($"{EscapeCsv(sourcePath)},copied,{fingerprint.Size},{fingerprint.Duration.TotalSeconds:0.###},{EscapeCsv(destinationPath)},");
                }
                catch (Exception ex)
                {
                    knownFingerprints.Remove(fingerprint);
                    errors++;
                    log.AppendLine($"{EscapeCsv(sourcePath)},error,{fingerprint.Size},{fingerprint.Duration.TotalSeconds:0.###},,{EscapeCsv(ex.Message)}");
                }
            }

            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);

            string summary = $"Archive completed.\n\nCopied: {copied}\nSkipped duplicates: {skippedDuplicates}\nSkipped (over 1m30s): {skippedTooLong}\nErrors: {errors}\n\nLog: {logPath}";
            lblTotalTime.Invoke((MethodInvoker)delegate
            {
                lblTotalTime.Text = $"Archived: {copied} copied, {skippedDuplicates} dup, {skippedTooLong} too long";
            });

            return summary;
        }

        private static bool IsUnderDirectory(string filePath, string directoryPath)
        {
            string normalizedFile = Path.GetFullPath(filePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedDirectory = Path.GetFullPath(directoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return normalizedFile.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedFile, normalizedDirectory, StringComparison.OrdinalIgnoreCase);
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