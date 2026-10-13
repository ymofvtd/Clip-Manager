using System.Runtime.CompilerServices;

namespace VideoTrayApp;

// Explicit marshaling also works in native prototype harnesses without Application.Run.
internal static class UiThread
{
    internal static SwitchAwaiter Switch(Control control) => new(control);
    internal readonly struct SwitchAwaiter(Control control) : INotifyCompletion
    {
        internal SwitchAwaiter GetAwaiter() => this;
        public bool IsCompleted => control.IsDisposed || !control.InvokeRequired;
        public void GetResult() { }
        public void OnCompleted(Action continuation)
        {
            try { control.BeginInvoke(continuation); }
            catch (InvalidOperationException) { continuation(); }
        }
    }
}
