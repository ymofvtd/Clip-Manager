namespace VideoTrayApp;

internal enum ArchiveRuntimeState { Stopped, Running, Watching, WaitingForStableFile, Paused, WaitingForForegroundWork, Suspended }
internal sealed record ArchiveStatus(bool Running, bool Paused, int Queued, string Current, int Percent,
    int Copied, int AlreadyArchived, int Failed, DateTimeOffset? LastSuccess, string Warning, string[] UnavailableRoots, string[] Log,
    ArchiveRuntimeState State = ArchiveRuntimeState.Stopped);
internal sealed record ArchivePending(string Path, int Attempts, DateTimeOffset NextAttempt, string Reason);

internal sealed class ArchiveWorker : IDisposable, IOperationProgress
{
    internal const int QueueCapacity = 4096;
    private readonly object gate = new();
    private readonly OperationCoordinator coordinator;
    private readonly ISet<string> extensions;
    private readonly string dataFolder;
    private readonly IArchiveClock clock;
    private readonly Dictionary<string, Job> queue = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> log = new();
    private CancellationTokenSource? stop;
    private CancellationTokenSource? cycle;
    private Task? task;
    private bool paused, reconcileRequested, disposed;
    private bool suspended;
    private string current = "", warning = "";
    private List<string> unavailableRoots = [];
    private int percent, copied, archived, failed;
    private DateTimeOffset? lastSuccess;
    private DateTimeOffset nextStatus;
    internal event Action<ArchiveStatus>? StatusChanged;
    internal event Action? ArchiveChanged;
    internal ArchiveSettings Settings { get; private set; } = new();
    internal bool Running { get { lock (gate) return task is { IsCompleted: false }; } }
    internal ArchiveStatus Status { get { lock (gate) return Snapshot(); } }
    internal IReadOnlyList<ArchivePending> Pending
    {
        get { lock (gate) return queue.Take(QueueCapacity).Select(p => new ArchivePending(p.Key, p.Value.Attempts, p.Value.Due, p.Value.Reason)).ToArray(); }
    }
    public CancellationToken CancellationToken => stop?.Token ?? CancellationToken.None;

    internal ArchiveWorker(OperationCoordinator coordinator, ISet<string> extensions, string dataFolder, IArchiveClock? clock = null)
    { this.coordinator = coordinator; this.extensions = extensions; this.dataFolder = dataFolder; this.clock = clock ?? new ArchiveClock(); }

    internal void Start(ArchiveSettings settings)
    {
        settings = settings.Clone();
        settings.Validate(requireAvailable: false);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (task is { IsCompleted: false }) throw new InvalidOperationException("Stop the current Archive run before changing settings.");
            stop?.Dispose();
            stop = new();
            Settings = settings;
            queue.Clear(); log.Clear();
            copied = archived = failed = percent = 0;
            current = warning = ""; paused = suspended = false; lastSuccess = null; reconcileRequested = true;
            task = Task.Run(() => Run(stop.Token));
            _ = task.ContinueWith(_ => Publish(), TaskScheduler.Default);
        }
        Publish();
    }
    internal void Pause()
    {
        lock (gate) { paused = true; cycle?.Cancel(); }
        Publish();
    }
    internal void Resume()
    {
        lock (gate) { paused = false; reconcileRequested = true; }
        Publish();
    }
    internal async Task StopAsync()
    {
        Task? running;
        lock (gate) { stop?.Cancel(); cycle?.Cancel(); running = task; }
        if (running is not null) await running;
    }
    internal void RequestReconciliation() { lock (gate) reconcileRequested = true; }

    private void Run(CancellationToken ct)
    {
        IEnumerator<string>? scan = null;
        var scanErrors = new List<string>();
        var limiter = new IoRateLimiter(Settings.Speed == ArchiveSpeed.Slow && Settings.MegabytesPerSecond is { } cap
            ? (long)cap * 1_000_000 : null, clock);
        var schedule = new ArchiveSchedule(Settings, clock);
        ClipArchiver? archiver = null;
        DateTimeOffset nextReconcile = clock.UtcNow;
        bool needsIndex = true, wasPaused = false;
        try
        {
            // Subscribe before the first archive/source scan; callbacks only enqueue metadata work.
            RefreshWatchers();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                bool isPaused;
                lock (gate) isPaused = paused;
                if (isPaused || coordinator.ForegroundActive)
                {
                    wasPaused = true;
                    clock.Delay(TimeSpan.FromMilliseconds(100), ct);
                    continue;
                }
                if (wasPaused) { schedule.AfterPause(); needsIndex = true; wasPaused = false; }
                bool rescan;
                lock (gate)
                {
                    rescan = scan is null && (reconcileRequested || clock.UtcNow >= nextReconcile);
                    if (rescan) reconcileRequested = false;
                    cycle?.Dispose();
                    cycle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                }
                using var lease = coordinator.Background(ct, out var coordinatorToken);
                using var contentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cycle.Token, coordinatorToken);
                var workToken = contentCancellation.Token;
                try
                {
                    if (needsIndex || rescan)
                    {
                        RefreshWatchers();
                        SetIndeterminate("Indexing and verifying archive locations...");
                        archiver ??= new ClipArchiver(Settings, dataFolder, limiter);
                        var errors = new List<string>();
                        archiver.Reconcile(extensions, errors, workToken, () => Yield(workToken), this);
                        lock (gate)
                        {
                            if (errors.Count > 0) warning = $"Archive scan incomplete: {errors.Count} unreadable files/folders.";
                            else if (failed == 0 && unavailableRoots.Count == 0) warning = "";
                        }
                        foreach (string error in errors.Take(20)) AddLog(error);
                        needsIndex = false;
                        lock (gate) suspended = false;
                    }
                    if (rescan || scan is null && nextReconcile == DateTimeOffset.MinValue)
                    {
                        scanErrors.Clear();
                        scan = ArchivePaths.Videos(Settings.Sources, extensions, scanErrors, ct,
                            () => Yield(ct)).GetEnumerator();
                        nextReconcile = clock.UtcNow.AddSeconds(Settings.ReconcileSeconds);
                    }
                    // Incremental discovery keeps memory and the event queue bounded.
                    if (scan is not null)
                    {
                        for (int i = 0; i < 128; i++)
                        {
                            workToken.ThrowIfCancellationRequested();
                            lock (gate) { if (queue.Count >= QueueCapacity) break; }
                            if (!scan.MoveNext())
                            {
                                scan.Dispose(); scan = null;
                                foreach (string error in scanErrors.Take(20)) AddLog(error);
                                if (scanErrors.Count > 0) lock (gate) warning = $"Source scan incomplete: {scanErrors.Count} unreadable files/folders. See log.";
                                break;
                            }
                            Enqueue(scan.Current, debounce: false);
                        }
                    }
                    Job? job;
                    string? path;
                    lock (gate)
                    {
                        var ready = queue.FirstOrDefault(pair => pair.Value.Due <= clock.UtcNow);
                        path = ready.Key; job = ready.Value;
                    }
                    if (path is null || job is null)
                    {
                        if (!Settings.WatchContinuously && scan is null && Status.Queued == 0) break;
                        clock.Delay(TimeSpan.FromMilliseconds(100), workToken);
                        continue;
                    }
                    try
                    {
                        var info = new FileInfo(path);
                        if (!info.Exists)
                        {
                            RemoveJob(path, job);
                            lock (gate) { warning = $"Source disappeared before protection: {path}"; if (job.Attempts == 0) failed++; }
                            AddLog(warning); continue;
                        }
                        if (job.Size != info.Length || job.Time != info.LastWriteTimeUtc)
                        {
                            job.Size = info.Length; job.Time = info.LastWriteTimeUtc;
                            job.Due = clock.UtcNow.AddSeconds(Settings.SettlingSeconds);
                            AddLog($"Pending: still growing: {path}");
                            continue;
                        }
                        workToken.ThrowIfCancellationRequested();
                        SetIndeterminate($"Waiting to start: {path}");
                        schedule.BeforeClip(workToken);
                        var result = archiver!.Process(path, this, workToken);
                        if (result.Outcome is ArchiveOutcome.CopiedAndVerified or ArchiveOutcome.AlreadyArchived)
                        {
                            RemoveJob(path, job);
                            lock (gate)
                            {
                                if (result.Outcome == ArchiveOutcome.CopiedAndVerified) copied++; else archived++;
                                if (job.Attempts > 0) failed = Math.Max(0, failed - 1);
                                if (failed == 0 && warning.StartsWith("Pending/failed clips remain:", StringComparison.Ordinal)) warning = "";
                                lastSuccess = clock.UtcNow;
                            }
                            AddLog($"{(result.Outcome == ArchiveOutcome.CopiedAndVerified ? "Copied and verified" : "Already archived")}: {path}");
                            if (result.Outcome == ArchiveOutcome.CopiedAndVerified) ArchiveChanged?.Invoke();
                        }
                        else if (result.DestinationUnavailable)
                        {
                            job.Due = clock.UtcNow.AddSeconds(5);
                            throw new ArchiveDestinationException(result.Message ?? "Destination unavailable/full.");
                        }
                        else Retry(path, job, result.Message ?? "Clip unavailable.");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (ArchiveDestinationException) { throw; }
                    catch (Exception ex) { Retry(path, job, ex.Message); }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    wasPaused = true; needsIndex = true;
                    AddLog("Pending: background work interrupted; completed copies retained.");
                }
                catch (Exception ex)
                {
                    // Keep the queue while the destination/index is unavailable, including disk-full failures.
                    lock (gate) { warning = $"Archive suspended: {ex.Message}"; suspended = true; }
                    AddLog(warning);
                    Publish();
                    // Release content access before backing off; foreground work has priority.
                    lease.Dispose();
                    try { clock.Delay(TimeSpan.FromSeconds(5), workToken); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { wasPaused = true; }
                    needsIndex = true;
                }
                Publish();
            }
        }
        catch (OperationCanceledException) { AddLog("Stopped. Completed verified copies retained; pending work will be rediscovered on Start."); }
        catch (Exception ex) { lock (gate) warning = "Archive stopped: " + ex.Message; AddLog(warning); }
        finally
        {
            scan?.Dispose();
            lock (gate)
            {
                foreach (var watcher in watchers.Values) watcher.Dispose();
                watchers.Clear(); cycle?.Dispose(); cycle = null; current = "";
            }
            // Publish after this task finishes so Running is accurate.
            try { AtomicJson.Save(Path.Combine(dataFolder, "archive_log.json"), log.ToArray()); } catch { }
        }
    }

    private void Yield(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); if (Settings.Speed == ArchiveSpeed.Slow) clock.Delay(TimeSpan.FromMilliseconds(1), ct); }
    private void Retry(string path, Job job, string reason)
    {
        lock (gate)
        {
            job.Attempts++;
            job.Reason = reason;
            job.Due = clock.UtcNow.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(job.Attempts - 1, 6))));
            warning = $"Pending/failed clips remain: {path}: {reason}";
            if (job.Attempts == 1) failed++;
        }
        AddLog($"Pending (retry {job.Attempts}): {path}: {reason}");
        if (!Settings.WatchContinuously && job.Attempts >= 3) { RemoveJob(path, job); AddLog($"Failed after 3 attempts; source unchanged: {path}"); }
        if (!Directory.Exists(Settings.Destination)) throw new DirectoryNotFoundException(Settings.Destination);
    }
    private void RemoveJob(string path, Job job)
    { lock (gate) if (queue.TryGetValue(path, out var currentJob) && ReferenceEquals(job, currentJob)) queue.Remove(path); }

    internal void Enqueue(string path, bool debounce)
    {
        if (!extensions.Contains(Path.GetExtension(path))) { if (Directory.Exists(path)) RequestReconciliation(); return; }
        lock (gate)
        {
            if (queue.Count >= QueueCapacity && !queue.ContainsKey(path)) { reconcileRequested = true; return; }
            if (queue.TryGetValue(path, out var previous))
            {
                if (!debounce) return;
                if (previous.Attempts > 0) failed = Math.Max(0, failed - 1);
            }
            var job = new Job { Due = clock.UtcNow.AddSeconds(Settings.SettlingSeconds) };
            try { var info = new FileInfo(path); job.Size = info.Length; job.Time = info.LastWriteTimeUtc; } catch { }
            queue[path] = job;
        }
    }
    private void RefreshWatchers()
    {
        lock (gate)
        {
            foreach (var pair in watchers.ToArray())
                if (!Directory.Exists(pair.Key)) { pair.Value.Dispose(); watchers.Remove(pair.Key); }
            var unavailable = new List<string>();
            foreach (string root in Settings.Sources)
            {
                if (watchers.ContainsKey(root)) continue;
                FileSystemWatcher? watcher = null;
                try
                {
                    ActionPreset.ValidateFolder(root);
                    if (!Settings.WatchContinuously) continue;
                    watcher = new(root) { IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                    watcher.Created += (_, e) => Enqueue(e.FullPath, true);
                    watcher.Changed += (_, e) => Enqueue(e.FullPath, true);
                    watcher.Deleted += (_, _) => RequestReconciliation();
                    watcher.Renamed += (_, e) => { Enqueue(e.FullPath, true); RequestReconciliation(); };
                    watcher.Error += (_, _) =>
                    {
                        lock (gate) { reconcileRequested = true; watchers.Remove(root); }
                        watcher.Dispose();
                    };
                    watchers.Add(root, watcher);
                    watcher.EnableRaisingEvents = true;
                }
                catch (Exception ex) { watcher?.Dispose(); unavailable.Add(root); AddLog($"Unavailable root: {root}: {ex.Message}"); }
            }
            unavailableRoots = unavailable;
            if (unavailable.Count > 0) warning = "Unavailable source roots: " + string.Join("; ", unavailable);
        }
    }
    public void Report(int value, int total, string message)
    { lock (gate) { current = message; percent = total > 0 ? (int)((long)value * 100 / total) : 0; } Publish(); }
    public void SetIndeterminate(string message) => Report(0, 0, message);
    private void AddLog(string message)
    {
        lock (gate) { log.Enqueue($"{clock.UtcNow:HH:mm:ss} {message}"); while (log.Count > 200) log.Dequeue(); }
    }
    private ArchiveStatus Snapshot()
    {
        bool running = task is { IsCompleted: false };
        var state = !running ? ArchiveRuntimeState.Stopped : paused ? ArchiveRuntimeState.Paused
            : coordinator.ForegroundActive ? ArchiveRuntimeState.WaitingForForegroundWork : suspended ? ArchiveRuntimeState.Suspended
            : queue.Count > 0 && queue.All(p => p.Value.Due > clock.UtcNow) ? ArchiveRuntimeState.WaitingForStableFile
            : queue.Count == 0 && Settings.WatchContinuously ? ArchiveRuntimeState.Watching : ArchiveRuntimeState.Running;
        return new(running, paused, queue.Count, current, percent, copied, archived, failed, lastSuccess, warning,
            unavailableRoots.ToArray(), log.ToArray(), state);
    }
    private void Publish()
    {
        ArchiveStatus snapshot;
        lock (gate)
        {
            if (clock.UtcNow < nextStatus && task is { IsCompleted: false }) return;
            nextStatus = clock.UtcNow.AddMilliseconds(250);
            snapshot = Snapshot();
        }
        StatusChanged?.Invoke(snapshot);
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; stop?.Cancel(); cycle?.Cancel(); }
        task?.GetAwaiter().GetResult();
        stop?.Dispose();
    }
    private sealed class Job
    {
        internal long Size = -1;
        internal DateTime Time;
        internal DateTimeOffset Due;
        internal int Attempts;
        internal string Reason = "Waiting for a stable file or the next scheduled copy";
    }
    private sealed class ArchiveDestinationException(string message) : IOException(message);
}
