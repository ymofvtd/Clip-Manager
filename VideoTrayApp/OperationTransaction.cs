using Microsoft.VisualBasic.FileIO;

namespace VideoTrayApp;

// Only preset runs opt in. Existing interactive operations retain their behavior.
internal sealed class OperationTransaction : IDisposable
{
    private static readonly AsyncLocal<OperationTransaction?> active = new();
    internal static bool IsActive => active.Value is not null;
    private readonly List<(Action Restore, bool Cleanup)> undo = [];
    private readonly List<(string Staged, string Recovery)> recycled = [];
    private bool finished;

    internal OperationTransaction()
    {
        if (IsActive) throw new InvalidOperationException("An operation is already running.");
        active.Value = this;
    }

    internal static void Move(string source, string destination)
    {
        File.Move(source, destination);
        active.Value?.undo.Add((() => File.Move(destination, source), false));
    }

    internal static void Copy(string source, string destination, bool overwrite = false)
    {
        if (overwrite && IsActive) throw new IOException("Transactional copies cannot overwrite files.");
        bool existed = File.Exists(destination);
        try { File.Copy(source, destination, overwrite); }
        catch
        {
            if (IsActive && !existed && File.Exists(destination)) File.Delete(destination);
            throw;
        }
        active.Value?.undo.Add((() => File.Delete(destination), true));
    }

    internal static void CreateDirectory(string path)
    {
        if (Directory.Exists(path)) return;
        Directory.CreateDirectory(path);
        active.Value?.undo.Add((() => Directory.Delete(path, recursive: false), true));
    }

    internal static void Recycle(string path)
    {
        var transaction = active.Value;
        if (transaction is null)
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            return;
        }
        // Staged files have a non-video extension so refills/scans cannot select them.
        string staged = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        string recovery = staged + ".recovery";
        File.Move(path, staged);
        transaction.undo.Add((() =>
        {
            if (File.Exists(staged)) File.Move(staged, path);
            else File.Move(recovery, path);
            if (File.Exists(recovery)) File.Delete(recovery);
        }, false));
        transaction.recycled.Add((staged, recovery));
    }

    internal void Commit(CancellationToken ct, Action<string>? recycleFile = null)
    {
        // Keep restorable content until every Recycle Bin operation succeeds.
        foreach (var item in recycled)
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(item.Staged, item.Recovery);
        }
        foreach (var item in recycled)
        {
            ct.ThrowIfCancellationRequested();
            if (recycleFile is not null) recycleFile(item.Staged);
            else FileSystem.DeleteFile(item.Staged, UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        }
        ct.ThrowIfCancellationRequested();
        finished = true;
        active.Value = null;
        // Cleanup failures do not invalidate a committed operation or discard recovery files.
        foreach (var item in recycled)
            try { File.Delete(item.Recovery); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal void Rollback()
    {
        active.Value = null;
        var errors = new List<string>();
        for (int i = undo.Count - 1; i >= 0; i--)
        {
            // Retain backup copies when restoring a moved original failed.
            if (undo[i].Cleanup && errors.Count > 0) continue;
            try { undo[i].Restore(); } catch (Exception ex) { errors.Add(ex.Message); }
        }
        finished = true;
        if (errors.Count > 0)
            throw new IOException("Some changes could not be restored. Recovery files were retained.\n" + string.Join("\n", errors));
    }

    public void Dispose()
    {
        if (!finished) Rollback();
    }
}
