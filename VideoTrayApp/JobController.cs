namespace VideoTrayApp;

internal sealed record JobOutcome(JobState State, string Details)
{
    internal static JobOutcome Complete(string details) => new(JobState.Completed, details);
}
internal sealed record ProgressSnapshot(int Current, int Total, string Message);

internal sealed class JobController : IOperationProgress, IDisposable
{
    private readonly OperationCoordinator coordinator;
    private readonly ActivityHistory history;
    private readonly string dataFolder;
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private ProgressSnapshot snapshot = new(0, 0, "Ready");
    internal Task? ActiveTask { get; private set; }
    internal bool Busy => ActiveTask is { IsCompleted: false };
    internal JobState State { get; private set; } = JobState.Editing;
    internal string Title { get; private set; } = "Ready";
    internal DateTimeOffset? Started { get; private set; }
    internal ProgressSnapshot Progress { get { lock (gate) return snapshot; } }
    public CancellationToken CancellationToken => cancellation?.Token ?? CancellationToken.None;
    internal JobController(OperationCoordinator coordinator, ActivityHistory history, string dataFolder)
    { this.coordinator = coordinator; this.history = history; this.dataFolder = dataFolder; }

    internal async Task<JobOutcome?> RunAsync(string title, string context, bool analyze,
        Func<IOperationProgress, CancellationToken, JobOutcome> work)
    {
        if (Busy) return null;
        cancellation?.Dispose(); cancellation = new();
        var token = cancellation.Token;
        Title = title; State = analyze ? JobState.Analyzing : JobState.Running;
        snapshot = new(0, 0, analyze ? "Analyzing…" : "Starting…");
        var started = DateTimeOffset.Now;
        Started = started;
        var record = new JobRecord(title, context, started, null, State, "Operation started; no final outcome yet.");
        history.Add(record);
        string persistenceWarning = Persist();
        // Store the task before its first asynchronous boundary so concurrent tray commands cannot enter.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ActiveTask = completion.Task;
        JobOutcome result;
        try
        {
            using var lease = await coordinator.ForegroundAsync();
            result = await Task.Run(() => { token.ThrowIfCancellationRequested(); return work(this, token); }, token);
        }
        catch (OperationCanceledException ex)
        { result = new(JobState.Cancelled, ex.Message + "\nCancellation stops at safe boundaries. Inspect results for retained or recovered work."); }
        catch (Exception ex) { result = new(JobState.Failed, ex.Message); }
        State = result.State;
        if (persistenceWarning.Length > 0) result = result with { Details = result.Details + "\n" + persistenceWarning };
        lock (gate) snapshot = new(0, 0, result.Details);
        history.Replace(record, record with { State = result.State, Finished = DateTimeOffset.Now, Details = result.Details });
        string saveWarning = Persist();
        if (saveWarning.Length > 0) { result = result with { Details = result.Details + "\n" + saveWarning }; lock (gate) snapshot = snapshot with { Message = result.Details }; }
        completion.TrySetResult();
        return result;
    }
    private string Persist()
    { try { history.Save(dataFolder); return ""; } catch (Exception ex) { return "History could not be saved: " + ex.Message; } }
    internal void Cancel()
    {
        if (!Busy) return;
        State = JobState.Cancelling; cancellation?.Cancel();
        SetIndeterminate("Cancelling — waiting for the next safe boundary or recovery to finish…");
    }
    public void Report(int current, int total, string message) { lock (gate) snapshot = new(current, total, message); }
    public void SetIndeterminate(string message) => Report(0, 0, message);
    public void Dispose() { cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null; }
}
