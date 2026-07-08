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
        private CancellationTokenSource? operationCts;

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
            UpdateFolderDisplay();

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
                Text = "Video Tray"
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
                    UpdateVideoCount();
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

        private void UpdateVideoCount()
        {
            try
            {
                int count = CountVideosInFolder(folderPath);
                if (InvokeRequired)
                {
                    Invoke(UpdateVideoCountDisplay, count);
                    return;
                }

                UpdateVideoCountDisplay(count);
            }
            catch
            {
                // Ignore count errors
            }
        }

        private static int CountVideosInFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return 0;

            return Directory.EnumerateFiles(folder)
                .Count(path => DefaultVideoExts.Contains(Path.GetExtension(path)));
        }

        private void UpdateVideoCountDisplay(int count)
        {
            lblVideoCount.Text = count == 1 ? "1 video" : $"{count} videos";
        }

        private sealed class EmbeddedOperationProgress : IOperationProgress
        {
            private readonly Form1 form;

            public EmbeddedOperationProgress(Form1 form)
            {
                this.form = form;
            }

            public CancellationToken CancellationToken =>
                form.operationCts?.Token ?? CancellationToken.None;

            public void Report(int current, int total, string message) =>
                form.ReportEmbeddedProgress(current, total, message);

            public void SetIndeterminate(string message) =>
                form.ReportEmbeddedProgress(0, 0, message);
        }

        private void BeginEmbeddedOperation(string title)
        {
            operationCts?.Dispose();
            operationCts = new CancellationTokenSource();

            lblProgressStatus.Text = title;
            progressBarMain.Style = ProgressBarStyle.Marquee;
            progressBarMain.MarqueeAnimationSpeed = 30;
            btnCancelOperation.Visible = true;
            btnCancelOperation.Enabled = true;
        }

        private void ReportEmbeddedProgress(int current, int total, string message)
        {
            if (InvokeRequired)
            {
                Invoke(ReportEmbeddedProgress, current, total, message);
                return;
            }

            lblProgressStatus.Text = message;
            if (total <= 0)
            {
                progressBarMain.Style = ProgressBarStyle.Marquee;
                progressBarMain.MarqueeAnimationSpeed = 30;
                return;
            }

            progressBarMain.Style = ProgressBarStyle.Continuous;
            progressBarMain.Maximum = Math.Max(total, 1);
            progressBarMain.Value = Math.Min(Math.Max(current, 0), progressBarMain.Maximum);
        }

        private void CompleteEmbeddedOperation(string message)
        {
            if (InvokeRequired)
            {
                Invoke(CompleteEmbeddedOperation, message);
                return;
            }

            lblProgressStatus.Text = message;
            progressBarMain.Style = ProgressBarStyle.Continuous;
            progressBarMain.Value = progressBarMain.Maximum > 0 ? progressBarMain.Maximum : 0;
            btnCancelOperation.Visible = false;
            btnCancelOperation.Enabled = false;
            operationCts?.Dispose();
            operationCts = null;
        }

        private void btnCancelOperation_Click(object? sender, EventArgs e)
        {
            operationCts?.Cancel();
            btnCancelOperation.Enabled = false;
            lblProgressStatus.Text = "Cancelling...";
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
            btnPrepare.Enabled = enabled;
            btnShuffleRandom.Enabled = enabled;
            btnArchive.Enabled = enabled;
            btnBrowseFolder.Enabled = enabled;
            if (!enabled)
                btnCancelOperation.Enabled = enabled;
        }

        private async Task RunWithProgressAsync(
            string title,
            Func<IOperationProgress, CancellationToken, Task<string?>> work,
            bool refreshVideoCountOnComplete = true,
            bool showSuccessMessage = false,
            string? successMessage = null)
        {
            SetButtonsEnabled(false);
            BeginEmbeddedOperation(title);

            var progress = new EmbeddedOperationProgress(this);
            var workTask = Task.Run(() => work(progress, progress.CancellationToken));
            try
            {
                string? resultMessage = await workTask;
                CompleteEmbeddedOperation("Done");

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
                CompleteEmbeddedOperation("Cancelled");
                MessageBox.Show("Operation cancelled.", "Cancelled", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                CompleteEmbeddedOperation("Failed");
                MessageBox.Show($"Operation failed:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetButtonsEnabled(true);

                if (refreshVideoCountOnComplete)
                {
                    try
                    {
                        UpdateVideoCount();
                    }
                    catch
                    {
                        // Ignore refresh errors
                    }
                }
            }
        }

        private async Task RunCheckForUpdateAsync()
        {
            SetButtonsEnabled(false);
            BeginEmbeddedOperation("Check for Updates");

            try
            {
                var progress = new EmbeddedOperationProgress(this);
                progress.SetIndeterminate("Checking for updates...");

                UpdateCheckResult result = await AppUpdater.CheckForUpdateAsync(progress.CancellationToken);
                CompleteEmbeddedOperation("Ready");

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
                    refreshVideoCountOnComplete: false,
                    showSuccessMessage: true);

                Application.Exit();
            }
            catch (OperationCanceledException)
            {
                CompleteEmbeddedOperation("Cancelled");
                MessageBox.Show("Update cancelled.", "Cancelled", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                CompleteEmbeddedOperation("Failed");
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
            UpdateVideoCount();
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
            this.ShowInTaskbar = true;
            UpdateFolderDisplay();
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
                UpdateFolderDisplay();
                this.BringToFront();
            }
        }

        private void SetFolderPath(object? sender, EventArgs e)
        {
            BrowseForWorkingFolder();
        }

        private void btnBrowseFolder_Click(object? sender, EventArgs e)
        {
            BrowseForWorkingFolder();
        }

        private void BrowseForWorkingFolder()
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Select the working folder to manage",
                SelectedPath = Directory.Exists(folderPath) ? folderPath : string.Empty
            };

            if (dlg.ShowDialog() != DialogResult.OK)
                return;

            ApplyWorkingFolder(dlg.SelectedPath);
        }

        private void ApplyWorkingFolder(string path)
        {
            folderPath = path;
            SaveFolderPathToConfig(folderPath);
            UpdateFolderDisplay();

            watcher?.Dispose();
            SetupWatcher();
            UpdateVideoCount();
        }

        private void UpdateFolderDisplay()
        {
            if (InvokeRequired)
            {
                Invoke(UpdateFolderDisplay);
                return;
            }

            txtSelectedFolder.Text = string.IsNullOrEmpty(folderPath)
                ? "(not set)"
                : folderPath;
            UpdateVideoCount();
        }

        private bool TryGetWorkingFolder(out string folder, string actionName)
        {
            folder = folderPath;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                return true;

            MessageBox.Show(
                $"Please select a working folder before running {actionName}.",
                "Folder Required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        // Button handlers now call the internal C# implementations
        private async void btnShuffleAndName_Click(object? sender, EventArgs e)
        {
            if (!TryGetWorkingFolder(out string targetFolder, "Shuffle & Name"))
                return;
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
            if (!TryGetWorkingFolder(out string targetFolder, "Name by 10s"))
                return;
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

        private async void btnPrepare_Click(object? sender, EventArgs e)
        {
            using var sourceDialog = new FolderBrowserDialog
            {
                Description = "Select folder containing numbered clips (e.g., 349.mp4)"
            };
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
                sourceDialog.SelectedPath = folderPath;
            if (sourceDialog.ShowDialog() != DialogResult.OK)
                return;

            string sourceFolder = sourceDialog.SelectedPath;

            // Select destination folder or create new one
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
            if (!TryGetWorkingFolder(out string targetFolder, "Shuffle Random"))
                return;

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
            if (!TryGetWorkingFolder(out string sourceFolder, "Archive"))
                return;

            await RunWithProgressAsync(
                "Archive Videos",
                (progress, ct) => Task.FromResult<string?>(ArchiveVideos(sourceFolder, progress, ct)),
                showSuccessMessage: true);
        }

        //
        // Implementation of NameTenSteps (two-pass rename to clear namespace and then 0,10,20...)
        //
        private void NameTenSteps(string folder, int start, IOperationProgress? progress = null, CancellationToken cancellationToken = default)
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
        private void ShuffleAndNameStepTen(string folder, int start, int pad, string prefix, IOperationProgress? progress = null, CancellationToken cancellationToken = default)
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
                File.Move(current.FullName, finalPath);
                seq += 10;
            }
        }

        //
        // Implementation of ShuffleToRandom: renames all videos in folder to random character names
        //
        private void ShuffleToRandom(string folder, int length, IOperationProgress? progress = null, CancellationToken cancellationToken = default)
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
            }
        }

        //
        // Implementation of BatchNumberedVideos - moves numbered .mp4 files from source to destination,
        // keeping total duration under the specified limit. If moving a file exceeds the limit,
        // it stops and leaves that file in the source folder.
        //
        private void BatchNumberedVideos(string sourceFolder, string destinationFolder, TimeSpan durationLimit, IOperationProgress? progress = null, CancellationToken cancellationToken = default)
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
                return;

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

        }

        private static readonly TimeSpan MaxArchiveDuration = TimeSpan.FromSeconds(90);

        private string ArchiveVideos(string sourceFolder, IOperationProgress progress, CancellationToken cancellationToken)
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

            for (int i = 0; i < sourceFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sourcePath = sourceFiles[i];
                string fileName = Path.GetFileName(sourcePath);
                progress.Report(i + 1, sourceFiles.Count, $"Checking {fileName}...");

                if (!VideoFingerprintFactory.TryCreate(sourcePath, out var fingerprint, out _)
                    || fingerprint is null)
                {
                    errors++;
                    continue;
                }

                if (!knownFingerprints.Add(fingerprint))
                {
                    skippedDuplicates++;
                    continue;
                }

                if (fingerprint.Duration > MaxArchiveDuration)
                {
                    skippedTooLong++;
                    continue;
                }

                try
                {
                    string destinationPath = EnsureUniquePath(Path.Combine(archiveFolder, fileName));
                    progress.Report(i + 1, sourceFiles.Count, $"Copying {fileName}...");
                    File.Copy(sourcePath, destinationPath, overwrite: false);
                    copied++;
                }
                catch
                {
                    knownFingerprints.Remove(fingerprint);
                    errors++;
                }
            }

            return $"Archive completed.\n\nCopied: {copied}\nSkipped duplicates: {skippedDuplicates}\nSkipped (over 1m30s): {skippedTooLong}\nErrors: {errors}";
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