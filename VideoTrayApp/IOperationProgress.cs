namespace VideoTrayApp
{
    public interface IOperationProgress
    {
        CancellationToken CancellationToken { get; }
        void Report(int current, int total, string message);
        void SetIndeterminate(string message);
    }
}