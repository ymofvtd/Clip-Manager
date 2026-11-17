namespace VideoTrayApp;

internal sealed partial class CompilationHistoryWorkspace
{
    private Panel? historyPage;
    private DesktopButton? historyTab, changeDate, deleteCompilation, refreshHistory, viewClips;
    private DataGridView? historyTable;
    private DateTimePicker? editedDate;
    private Label? historySummary;
    private bool loadingHistory, reloadHistoryAgain;
    private CompilationSnapshot? SelectedCompilation => historyTable?.CurrentRow?.Tag as CompilationSnapshot;

    private void BuildHistoryPage(Panel pages, FlowLayoutPanel tabs)
    {
        historyPage = new() { Dock = DockStyle.Fill, AutoScroll = true };
        historyTable = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, AccessibleName = "Saved compilations" };
        foreach (var (title, width) in new[] { ("Compilation", 230), ("Publish date (local)", 170), ("Clips", 65), ("Folder", 280), ("Saved at (local)", 170) })
            historyTable.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = title, Width = width });
        historyTable.Columns[1].DefaultCellStyle.Format = historyTable.Columns[4].DefaultCellStyle.Format = "yyyy-MM-dd HH:mm";
        editedDate = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Width = 190,
            AccessibleName = "Selected compilation publish date (local)" };
        changeDate = DesktopTheme.Button("Change publish date", async () => await ChangeDateAsync());
        deleteCompilation = DesktopTheme.Button("Delete compilation…", async () => await DeleteCompilationAsync());
        deleteCompilation.Tag = CommandRole.Recycle;
        refreshHistory = DesktopTheme.Button("Refresh list", async () => await ReloadHistoryAsync());
        viewClips = DesktopTheme.Button("View saved clips", () =>
        { if (SelectedCompilation is { } snapshot) { ShowSavedClips(snapshot); ShowPage(true); } });
        var actions = DesktopTheme.Actions(DesktopTheme.Text("Publish date (local)"), editedDate, changeDate,
            viewClips, deleteCompilation, refreshHistory);
        actions.Dock = DockStyle.Top; actions.Padding = new Padding(20, 6, 16, 6);
        historySummary = DesktopTheme.Text("Loading saved compilations…"); historySummary.Dock = DockStyle.Bottom;
        historySummary.Padding = new Padding(20, 6, 16, 6);
        historyPage.Controls.Add(historyTable); historyPage.Controls.Add(historySummary); historyPage.Controls.Add(actions);
        void FitHistory()
        {
            bool compact = historyPage.ClientSize.Height - actions.Height - historySummary.Height < 220;
            historyTable.Dock = compact ? DockStyle.Top : DockStyle.Fill;
            if (compact) historyTable.Height = 280;
        }
        historyPage.SizeChanged += (_, _) => FitHistory();
        actions.SizeChanged += (_, _) => FitHistory();
        historyTable.SelectionChanged += (_, _) =>
        {
            if (SelectedCompilation is { } snapshot)
                editedDate.Value = ClampDate(editedDate, snapshot.PublishTimeUtc.LocalDateTime);
            RefreshEnabled();
        };
        historyTab = DesktopTheme.Button("Saved compilations", async () =>
        { ShowHistory(); await ReloadHistoryAsync(); });
        historyTab.Kind = ButtonKind.Tab; tabs.Controls.Add(historyTab); pages.Controls.Add(historyPage);
    }

    private static DateTime ClampDate(DateTimePicker picker, DateTime value) =>
        value < picker.MinDate ? picker.MinDate : value > picker.MaxDate ? picker.MaxDate : value;

    private void ShowHistory()
    {
        if (historyPage is null) return;
        configuration.Visible = resultPage.Visible = false; historyPage.Visible = true; historyPage.BringToFront();
        settingsTab.Selected = resultsTab.Selected = false; historyTab!.Selected = true;
    }

    internal async Task ReloadHistoryAsync()
    {
        if (!saving || historyTable is null) return;
        if (loadingHistory) { reloadHistoryAgain = true; return; }
        loadingHistory = true; RefreshEnabled();
        Guid? selected = SelectedCompilation?.Id;
        try
        {
            var snapshots = await Task.Run(() => shell.CompilationHistory.List(CancellationToken.None));
            await UiThread.Switch(this);
            if (IsDisposed) return;
            historyTable.Rows.Clear();
            foreach (var snapshot in snapshots)
            {
                int row = historyTable.Rows.Add(snapshot.Name, snapshot.PublishTimeUtc.LocalDateTime, snapshot.Clips.Count,
                    snapshot.Folder, snapshot.SavedAtUtc.LocalDateTime);
                historyTable.Rows[row].Tag = snapshot;
                if (snapshot.Id == selected) historyTable.CurrentCell = historyTable.Rows[row].Cells[0];
            }
            // Row tags are assigned after adding the row, so refresh the first selection explicitly.
            if (SelectedCompilation is { } current) editedDate!.Value = ClampDate(editedDate, current.PublishTimeUtc.LocalDateTime);
            historySummary!.Text = $"{snapshots.Count:N0} saved compilations. Future dates reserve clips. Records expire 30 days after publication; clip files stay in place.";
        }
        catch (Exception ex)
        {
            await UiThread.Switch(this);
            if (!IsDisposed) { historyTable.Rows.Clear(); historySummary!.Text = "History unavailable. " + ex.Message; status.Text = ex.Message; }
        }
        finally
        {
            await UiThread.Switch(this);
            if (!IsDisposed)
            {
                loadingHistory = false; RefreshEnabled();
                if (reloadHistoryAgain) { reloadHistoryAgain = false; await ReloadHistoryAsync(); }
            }
        }
    }

    private void ShowSavedClips(CompilationSnapshot saved)
    {
        results.SetRows(saved.Clips.Select(c => new ContextRow(c.Name, "", "Saved", Path.Combine(saved.Folder, c.Name), saved.Name,
            Path.GetDirectoryName(Path.Combine(saved.Folder, c.Name))!, "", WorkflowWorkspace.FormatSize(c.Size), Bytes: c.Size,
            Modified: saved.PublishTimeUtc, Identity: "SHA-256: " + c.Hash)).ToArray());
        results.SetContextSummary($"{saved.Name}. Publish date: {saved.PublishTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}. {saved.Clips.Count:N0} clips recorded.");
    }

    internal async Task ChangeDateAsync()
    {
        if (busy || loadingHistory || shell.IsBusy || SelectedCompilation is not { } snapshot) return;
        var published = new DateTimeOffset(editedDate!.Value).ToUniversalTime();
        await MutateHistoryAsync("Change compilation publish date", snapshot,
            ct => shell.CompilationHistory.ChangePublishedDate(snapshot.Id, published, ct), $"Publish date: {published.ToLocalTime():yyyy-MM-dd HH:mm}"
                + (published < DateTimeOffset.UtcNow.AddDays(-CompilationHistory.RecencyDays)
                    ? "\nThis publish date is over 30 days old. The record has expired and was removed." : ""));
    }

    internal async Task DeleteCompilationAsync()
    {
        if (busy || loadingHistory || shell.IsBusy || SelectedCompilation is not { } snapshot) return;
        if (MessageBox.Show(shell, $"Delete the saved record for “{snapshot.Name}” ({snapshot.PublishTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm})?\n\nClip files stay in place. Its clips will no longer be reserved by this record.",
            "Delete compilation record", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        await MutateHistoryAsync("Delete compilation record", snapshot,
            ct => shell.CompilationHistory.Delete(snapshot.Id, ct), "Record deleted. Clip files stay in place.");
    }

    private async Task MutateHistoryAsync(string title, CompilationSnapshot snapshot, Action<CancellationToken> action, string summary)
    {
        busy = true; RefreshEnabled();
        try
        {
            var outcome = await shell.RunJobAsync(title, $"Compilation: {snapshot.Name}\nRecord: {snapshot.Id}\n{summary}", false,
                (_, ct) => { action(ct); return JobOutcome.Complete(summary); });
            await UiThread.Switch(this);
            if (IsDisposed) return;
            status.Text = outcome?.Details ?? "Another operation is running. Try again when it finishes.";
            if (outcome?.State == JobState.Completed)
            {
                results.SetRows([]); results.SetContextSummary("History changed. Select a compilation to view its saved clips.");
                await ReloadHistoryAsync();
                await UiThread.Switch(this);
                if (!IsDisposed) ShowHistory();
            }
        }
        finally { await UiThread.Switch(this); if (!IsDisposed) { busy = false; RefreshEnabled(); } }
    }

    private void RefreshHistoryEnabled(bool enabled)
    {
        bool selected = enabled && !loadingHistory && SelectedCompilation is not null;
        if (editedDate is not null) editedDate.Enabled = selected;
        if (changeDate is not null) changeDate.Enabled = selected;
        if (deleteCompilation is not null) deleteCompilation.Enabled = selected;
        if (viewClips is not null) viewClips.Enabled = selected;
        if (refreshHistory is not null) refreshHistory.Enabled = enabled && !loadingHistory;
        if (historyTable is not null) historyTable.Enabled = enabled && !loadingHistory;
    }
}
