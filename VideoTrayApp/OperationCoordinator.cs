namespace VideoTrayApp;

internal sealed class OperationCoordinator : IDisposable
{
    private readonly SemaphoreSlim access = new(1, 1);
    private readonly object gate = new();
    private CancellationTokenSource interrupt = new();
    private int foreground;
    internal bool ForegroundActive { get { lock (gate) return foreground > 0; } }

    internal async Task<IDisposable> ForegroundAsync()
    {
        lock (gate) { foreground++; interrupt.Cancel(); }
        await access.WaitAsync();
        return new Lease(() =>
        {
            lock (gate)
            {
                foreground--;
                if (foreground == 0) { interrupt.Dispose(); interrupt = new(); }
                access.Release();
            }
        });
    }
    internal IDisposable Background(CancellationToken ct, out CancellationToken contentToken)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (foreground == 0 && access.Wait(0))
                {
                    var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, interrupt.Token);
                    contentToken = linked.Token;
                    return new Lease(() => { linked.Dispose(); access.Release(); });
                }
            }
            if (ct.WaitHandle.WaitOne(50)) ct.ThrowIfCancellationRequested();
        }
    }
    public void Dispose() { interrupt.Cancel(); interrupt.Dispose(); }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? callback = release;
        public void Dispose() => Interlocked.Exchange(ref callback, null)?.Invoke();
    }
}
