namespace VideoTrayApp;

internal sealed class InstanceActivation : IDisposable
{
    private readonly EventWaitHandle signal;
    private RegisteredWaitHandle? registration;
    internal InstanceActivation(string name) => signal = new(false, EventResetMode.AutoReset, name);
    internal void Listen(Control window, Action activate)
    {
        registration?.Unregister(null);
        registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
        {
            if (!window.IsHandleCreated || window.IsDisposed || window.Disposing) return;
            try { window.BeginInvoke(() => { if (!window.IsDisposed && !window.Disposing) activate(); }); }
            catch (InvalidOperationException) { }
        }, null, Timeout.Infinite, false);
    }
    internal void Signal() => signal.Set();
    public void Dispose() { registration?.Unregister(null); signal.Dispose(); }
}
