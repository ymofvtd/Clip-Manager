using Microsoft.VisualBasic.FileIO;

namespace VideoTrayApp;

// Presets and reviewed workspaces opt in. Legacy interactive adapters retain their behavior.
internal sealed class OperationTransaction : IDisposable
{
    private static readonly AsyncLocal<OperationTransaction?> active = new();
    internal static bool IsActive => active.Value is not null;
    private readonly List<(Action Restore, bool Cleanup, string Context)> undo = [];
    private readonly List<(string Staged, string Recovery)> recycled = [];
    private bool finished;
    internal List<string> CleanupWarnings { get; } = [];
    private readonly Dictionary<string, OwnedFile> files = new(StringComparer.OrdinalIgnoreCase);
    private sealed class OwnedFile(PhysicalFileId identity) { internal PhysicalFileId Identity = identity; }
    private OwnedFile Own(string path)
    {
        if (!files.TryGetValue(path, out var file)) files[path] = file = new(PhysicalFile.Read(path));
        return file;
    }
    private void Relocate(string source, string destination, OwnedFile file)
    { files.Remove(source); files[destination] = file; file.Identity = PhysicalFile.Read(destination); }

    internal OperationTransaction()
    {
        if (IsActive) throw new InvalidOperationException("An operation is already running.");
        active.Value = this;
    }

    internal static void Move(string source, string destination)
    {
        var transaction = active.Value;
        var owned = transaction?.Own(source);
        if (owned is not null) CheckOwned(source, owned.Identity);
        File.Move(source, destination);
        if (transaction is null) return;
        transaction.Relocate(source, destination, owned!);
        transaction.undo.Add((() =>
        {
            CheckOwned(destination, owned!.Identity); File.Move(destination, source);
            transaction.Relocate(destination, source, owned);
        }, false, $"Restore {destination} -> {source}"));
    }

    internal static void Copy(string source, string destination, bool overwrite = false)
    {
        if (overwrite && IsActive) throw new IOException("Transactional copies cannot overwrite files.");
        if (!IsActive) { File.Copy(source, destination, overwrite); return; }
        PhysicalFileId? owned = null;
        try
        {
            using var original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            owned = PhysicalFile.Read(output.SafeFileHandle);
            original.CopyTo(output); output.Flush(true);
        }
        catch
        {
            if (owned is not null && File.Exists(destination) && PhysicalFile.Read(destination) == owned) File.Delete(destination);
            throw;
        }
        var copy = active.Value!.Own(destination);
        active.Value.undo.Add((() => { CheckOwned(destination, copy.Identity); File.Delete(destination); }, true, $"Retained backup: {destination}"));
    }

    internal static void CreateDirectory(string path)
    {
        if (Directory.Exists(path)) return;
        Directory.CreateDirectory(path);
        active.Value?.undo.Add((() => Directory.Delete(path, recursive: false), true, $"Created folder: {path}"));
    }

    internal static void Recycle(string path)
    {
        var transaction = active.Value;
        if (transaction is null)
        {
            RecycleBin.Recycle(path);
            return;
        }
        // Staged files have a non-video extension so refills/scans cannot select them.
        string staged = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        string recovery = staged + ".recovery";
        var owned = transaction.Own(path);
        CheckOwned(path, owned.Identity);
        File.Move(path, staged);
        transaction.Relocate(path, staged, owned);
        transaction.undo.Add((() =>
        {
            string surviving = File.Exists(staged) ? staged : recovery;
            if (surviving == staged) CheckOwned(staged, owned.Identity);
            else if (transaction.files.TryGetValue(recovery, out var recoveryFile)) CheckOwned(recovery, recoveryFile.Identity);
            else throw new IOException("Recovery content is missing: " + recovery);
            File.Move(surviving, path);
            transaction.Relocate(surviving, path, owned);
            if (File.Exists(recovery))
            {
                if (!transaction.files.TryGetValue(recovery, out var recoveryOwner)) throw new IOException("Unowned recovery path left untouched: " + recovery);
                CheckOwned(recovery, recoveryOwner.Identity); File.Delete(recovery);
            }
        }, false, $"Restore {path}; staged: {staged}; recovery: {recovery}"));
        transaction.recycled.Add((staged, recovery));
    }

    internal void Commit(CancellationToken ct, Action<string>? recycleFile = null)
    {
        // Keep restorable content until every Recycle Bin operation succeeds.
        foreach (var item in recycled)
        {
            ct.ThrowIfCancellationRequested();
            CheckOwned(item.Staged, files[item.Staged].Identity);
            File.Copy(item.Staged, item.Recovery);
            Own(item.Recovery);
        }
        foreach (var item in recycled)
        {
            ct.ThrowIfCancellationRequested();
            CheckOwned(item.Staged, files[item.Staged].Identity);
            if (recycleFile is not null) recycleFile(item.Staged);
            else RecycleBin.Recycle(item.Staged);
        }
        ct.ThrowIfCancellationRequested();
        finished = true;
        active.Value = null;
        // Cleanup failures do not invalidate a committed operation or discard recovery files.
        foreach (var item in recycled)
            try { CheckOwned(item.Recovery, files[item.Recovery].Identity); File.Delete(item.Recovery); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { CleanupWarnings.Add("Recovery copy retained; cleanup needs attention: " + item.Recovery + ": " + ex.Message); }
    }

    internal void Rollback()
    {
        active.Value = null;
        var errors = new List<string>();
        for (int i = undo.Count - 1; i >= 0; i--)
        {
            // Retain backup copies when restoring a moved original failed.
            if (undo[i].Cleanup && errors.Count > 0) continue;
            try { undo[i].Restore(); } catch (Exception ex) { errors.Add(undo[i].Context + ": " + ex.Message); }
        }
        finished = true;
        if (errors.Count > 0)
            throw new IOException("Some changes could not be restored. Recovery files were retained.\n" + string.Join("\n", errors));
    }

    public void Dispose()
    {
        if (!finished) Rollback();
    }
    private static void CheckOwned(string path, PhysicalFileId identity)
    {
        if (!File.Exists(path) || PhysicalFile.Read(path) != identity)
            throw new IOException("Recovery path was replaced or disappeared; external content left untouched: " + path);
    }
}
