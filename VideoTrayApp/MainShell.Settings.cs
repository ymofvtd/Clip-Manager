using Microsoft.Win32;

namespace VideoTrayApp;

internal sealed partial class MainShell
{
    internal void OpenSettingsSection(string section)
    {
        Navigate("Settings");
        if (pages["Settings"] is TabControl tabs)
            foreach (TabPage page in tabs.TabPages) if (page.Text == section) { tabs.SelectedTab = page; break; }
    }
    private readonly Label updateStatus = DesktopTheme.Text("Updates are checked only when requested.");
    private UpdateCheckResult? availableUpdate;
    private bool checkingUpdate;
    private Task? updateTask;
    private Button? installUpdate;
    private async Task InspectUpdateRecoveryAsync()
    {
        string path = Path.Combine(dataFolder, "update_error.log");
        try
        {
            string details = await Task.Run(() => File.Exists(path) ? File.ReadAllText(path) : "");
            await UiThread.Switch(this);
            if (IsDisposed || shuttingDown || details.Length == 0) return;
            details = details[..Math.Min(details.Length, 64000)];
            persistentWarning += "\nAn earlier update needs attention. Open Activity or " + path + " for executable backup and recovery details.";
            updateStatus.Text = "Previous update failed. " + details;
            if (!history.Entries.Any(e => e.Action == "Update recovery" && e.Details == details))
            {
                history.Add(new("Update recovery", path, DateTimeOffset.Now, DateTimeOffset.Now, JobState.Failed, details));
                history.Save(dataFolder);
            }
        }
        catch (Exception ex) { await UiThread.Switch(this); if (!IsDisposed) persistentWarning += "\nUpdate recovery log could not be read: " + ex.Message; }
    }

    private Control BuildSettings()
    {
        var tabs = new TabControl { Dock = DockStyle.Top, Height = 700, AccessibleName = "Settings sections" };
        TabPage Section(string name)
        { var section = new TabPage(name) { AutoScroll = true }; tabs.TabPages.Add(section); return section; }
        var general = DesktopTheme.Stack();
        var startup = new CheckBox { Text = "Launch at Windows startup (opt in)", AutoSize = true };
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            startup.Checked = key?.GetValue("ClipsManager") is not null || key?.GetValue("VideoTrayApp") is not null;
            startupConfigured = true;
        }
        catch (Exception ex) { persistentWarning = "Startup preference could not be read: " + ex.Message; }
        startup.CheckedChanged += (_, _) =>
        {
            if (!startupConfigured) return;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (startup.Checked) key.SetValue("ClipsManager", "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue("ClipsManager", false);
                key.DeleteValue("VideoTrayApp", false);
            }
            catch (Exception ex)
            {
                persistentWarning = "Startup preference could not be saved: " + ex.Message;
                startupConfigured = false; startup.Checked = !startup.Checked; startupConfigured = true;
            }
        };
        CheckBox Preference(string text, bool value, Action<bool> update)
        {
            var control = new CheckBox { Text = text, Checked = value, AutoSize = true };
            control.CheckedChanged += (_, _) => { update(control.Checked); SavePreferences(); }; return control;
        }
        DesktopTheme.Add(general, startup);
        DesktopTheme.Add(general, Preference("Launch into tray after setup", preferences.LaunchIntoTray, value => preferences.LaunchIntoTray = value));
        DesktopTheme.Add(general, Preference("Close window to tray (Exit stops the application)", preferences.CloseToTray, value => preferences.CloseToTray = value));
        DesktopTheme.Add(general, Preference("Notify on operation completion / problems", preferences.Notifications, value => preferences.Notifications = value));
        var theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, AccessibleName = "Theme" };
        theme.Items.AddRange(["System", "Light", "Dark"]); theme.SelectedIndex = (int)preferences.Theme;
        theme.SelectedIndexChanged += (_, _) => { preferences.Theme = (AppTheme)theme.SelectedIndex; DesktopTheme.Current = preferences.Theme; DesktopTheme.Apply(this); SavePreferences(); };
        DesktopTheme.Add(general, DesktopTheme.Actions(DesktopTheme.Text("Theme"), theme));
        DesktopTheme.Add(general, DesktopTheme.Text("High Contrast uses Windows system colors. Working folders, duration tracking roots, and Archive sources are separate settings."));
        Section("General").Controls.Add(general);

        var tracking = DesktopTheme.Stack();
        DesktopTheme.Add(tracking, DesktopTheme.Text("Duration tracking writes owned duration markers (for example 90M30S.txt) in each root and its immediate subfolders. Other text files are preserved. Removing a root stops tracking; clips remain."));
        var roots = new SourceFolderList("Duration tracking roots — each root and its immediate subfolders") { Paths = trackingRoots };
        var totals = new DetailsBox { Multiline = true, ReadOnly = true, Height = 210, ScrollBars = ScrollBars.Vertical, AccessibleName = "Tracking totals and availability" };
        async Task ReadTotals()
        {
            var snapshot = roots.Paths;
            try
            {
                string value = await Task.Run(() => string.Join(Environment.NewLine, snapshot.Select(path =>
                {
                    try
                    {
                        if (!Directory.Exists(path)) return path + " — unavailable; saved root retained";
                        var markers = Directory.EnumerateFiles(path, "*.txt").Where(p => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), @"^\d+M\d+S\.txt$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
                        return path + " — " + (markers.Length == 0 ? "No current duration marker" : string.Join(", ", markers.Select(Path.GetFileName))) + "; inspected " + DateTime.Now.ToString("g");
                    }
                    catch (Exception ex) { return path + " — " + ex.Message; }
                })));
                await UiThread.Switch(this);
                if (!IsDisposed) totals.Text = value;
            }
            catch (Exception ex) { await UiThread.Switch(this); if (!IsDisposed) totals.Text = ex.Message; }
        }
        DesktopTheme.Add(tracking, roots);
        DesktopTheme.Add(tracking, DesktopTheme.Actions(DesktopTheme.Button("Save tracking roots", async () =>
        {
            if (jobs.Busy) { totals.Text = "Wait for foreground work to finish before changing tracking roots."; return; }
            try
            {
                var next = WatchFolderSettings.Normalize(roots.Paths); WatchFolderSettings.Save(dataFolder, next); trackingRoots = next;
                roots.Paths = next;
                BuildDurationStats();
                await Task.Run(() => tracker.SetFolders(next)); await UiThread.Switch(this); if (!IsDisposed) await ReadTotals();
            }
            catch (Exception ex) { await UiThread.Switch(this); if (!IsDisposed) totals.Text = "Tracking roots could not be saved: " + ex.Message; }
        }), DesktopTheme.Button("Refresh", async () =>
        {
            if (jobs.Busy) { totals.Text = "Tracking resumes and refreshes after foreground work."; return; }
            try { await Task.Run(() => tracker.Resume()); await UiThread.Switch(this); if (!IsDisposed) await ReadTotals(); }
            catch (Exception ex) { await UiThread.Switch(this); if (!IsDisposed) totals.Text = "Tracking could not be refreshed: " + ex.Message; }
        })));
        DesktopTheme.Add(tracking, totals); Section("Duration tracking").Controls.Add(tracking);
        _ = ReadTotals();

        var presetPage = Section("Presets");
        try
        {
            var manager = new PresetsForm(PresetStore.Load(Path.Combine(dataFolder, "presets.json")), Path.Combine(dataFolder, "presets.json"), workingFolder)
            { TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill };
            presetPage.Controls.Add(manager); manager.Show();
        }
        catch (Exception ex) { presetPage.Controls.Add(DesktopTheme.Text("Presets could not be loaded. Original settings remain intact. " + ex.Message)); }

        var updates = DesktopTheme.Stack();
        installUpdate = DesktopTheme.Button("Download and install update…", async () => await InstallUpdateAsync()); installUpdate.Enabled = false;
        DesktopTheme.Add(updates, DesktopTheme.Actions(DesktopTheme.Button("Check for updates", async () => await CheckUpdatesAsync()), installUpdate));
        DesktopTheme.Add(updates, updateStatus);
        DesktopTheme.Add(updates, DesktopTheme.Text("Applying an update waits for foreground work and drains Archive safely before replacing the executable. Navigation and cancellation remain available."));
        Section("Updates").Controls.Add(updates);
        var about = DesktopTheme.Stack();
        DesktopTheme.Add(about, DesktopTheme.Text($"Clips Manager v{AppUpdater.CurrentVersion.ToString(3)}\nWindows x64 • portable .NET 10 application\nData and diagnostics: {dataFolder}\nNo automatic telemetry."));
        DesktopTheme.Add(about, DesktopTheme.Button("Open data / log folder", () => { Directory.CreateDirectory(dataFolder); DesktopTheme.Open(dataFolder); }));
        DesktopTheme.Add(about, DesktopTheme.Button("Archive configuration", () => Navigate("Archive")));
        Section("About").Controls.Add(about); return tabs;
    }
    private async Task CheckUpdatesAsync()
    {
        if (checkingUpdate || shuttingDown) return;
        checkingUpdate = true; updateStatus.Text = "Checking…";
        try
        {
            availableUpdate = await AppUpdater.CheckForUpdateAsync(lifetime.Token);
            await UiThread.Switch(this);
            if (IsDisposed || shuttingDown) return;
            updateStatus.Text = availableUpdate.UpdateAvailable ? $"Available: v{availableUpdate.LatestVersion?.ToString(3)}. Review and install when ready." : $"Current version: v{availableUpdate.CurrentVersion.ToString(3)}.";
            if (installUpdate is not null) installUpdate.Enabled = availableUpdate.UpdateAvailable;
        }
        catch (OperationCanceledException) { await UiThread.Switch(this); if (!IsDisposed) updateStatus.Text = "Update check cancelled."; }
        catch (Exception ex) { await UiThread.Switch(this); if (!IsDisposed) updateStatus.Text = "Update check failed: " + ex.Message + " Retry when connected."; }
        finally { checkingUpdate = false; }
    }
    private async Task InstallUpdateAsync()
    {
        if (availableUpdate?.UpdateAvailable != true || jobs.Busy || shuttingDown || updateTask is { IsCompleted: false })
        { updateStatus.Text = "Finish foreground work before installing an update."; return; }
        if (MessageBox.Show(this, $"Install v{availableUpdate.LatestVersion?.ToString(3)} and restart after safely stopping background work?", "Install update",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        var update = availableUpdate;
        updateTask = ApplyUpdateAsync(update);
        await updateTask;
    }
    private async Task ApplyUpdateAsync(UpdateCheckResult update)
    {
        var outcome = await RunJobAsync("Install update", "Executable: " + Application.ExecutablePath, false, (progress, ct) =>
        {
            string download = AppUpdater.DownloadUpdateAsync(update.DownloadUrl!, update.LatestVersion!, (bytes, total) =>
            {
                if (total is > 0) progress.Report((int)(bytes * 100 / total.Value), 100, "Downloading update…");
                else progress.SetIndeterminate($"Downloading update: {bytes / 1_000_000d:0.0} MB");
            }, ct).GetAwaiter().GetResult();
            progress.SetIndeterminate("Draining background Archive before applying…");
            archive.StopAsync().GetAwaiter().GetResult(); ct.ThrowIfCancellationRequested();
            AppUpdater.ApplyUpdate(download);
            return JobOutcome.Complete("Update downloaded and scheduled. Restarting to apply.");
        });
        await UiThread.Switch(this);
        if (IsDisposed) return;
        if (outcome?.State == JobState.Completed) await ExitAsync();
        else
        {
            updateStatus.Text = outcome?.Details ?? "Update not started.";
            // Stop for update did not change the watch preference. Resume a failed/cancelled attempt.
            try { var saved = ArchiveSettings.Load(dataFolder); if (!shuttingDown && !archive.Running && saved.WatchContinuously) archive.Start(saved); }
            catch (Exception ex) { persistentWarning = "Archive could not resume after update: " + ex.Message; }
        }
    }
}
