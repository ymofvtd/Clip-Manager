using System.Collections.ObjectModel;

namespace VideoTrayApp;

internal sealed record ClipDateActionPlan(ReadOnlyCollection<PlannedChange> Changes, string? Destination)
{
    internal static ClipDateActionPlan Create(IReadOnlyList<DatedClip> selected, string? destination,
        IOperationProgress progress, CancellationToken ct)
    {
        if (selected.Count == 0) throw new ArgumentException("Select at least one clip from the results.");
        if (destination is not null)
        {
            destination = Path.GetFullPath(destination);
            ActionPreset.ValidateFolder(destination); ArchivePaths.RejectLinks(destination);
        }
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<PlannedChange>();
        foreach (var clip in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (!sources.Add(clip.Path)) continue;
            progress.Report(changes.Count, selected.Count, "Preparing selected clip: " + Path.GetFileName(clip.Path));
            VideoFiles.RequireMp4(clip.Path); ArchivePaths.RejectLinks(clip.Path);
            using var stream = new FileStream(clip.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != clip.Size || File.GetLastWriteTimeUtc(clip.Path) != clip.LastWriteUtc
                || PhysicalFile.Read(stream.SafeFileHandle) != clip.Physical
                || clip.Stamp is not null && PhysicalFile.ArchiveStamp(stream.SafeFileHandle, stream.Length) != clip.Stamp
                || ClipDateLookup.ReadCreationTime(stream, ct) != clip.Created)
                throw new IOException("Clip changed since lookup. Find clips again before acting: " + clip.Path);
            string target = "";
            if (destination is not null)
            {
                if (ClipIdentifier.SamePath(Path.GetDirectoryName(clip.Path)!, destination))
                    throw new IOException("A selected clip is already in the chosen destination. Choose another folder or deselect it: " + clip.Path);
                string name = Path.GetFileNameWithoutExtension(clip.Path), extension = Path.GetExtension(clip.Path);
                target = Path.Combine(destination, name + extension);
                for (int suffix = 1; reserved.Contains(target) || File.Exists(target) || Directory.Exists(target); suffix++)
                {
                    ct.ThrowIfCancellationRequested();
                    target = Path.Combine(destination, name + "__" + suffix + extension);
                }
                reserved.Add(target);
            }
            // Hash only the explicit selection, after lookup metadata has been checked on a protected handle.
            changes.Add(new(PlannedFile.Read(clip.Path, ct), target));
        }
        return new(changes.AsReadOnly(), destination);
    }

    internal IReadOnlyList<ContextRow> ReviewRows() => Changes.Select(c => new ContextRow(c.File.Path, "",
        Destination is null ? "Recycle" : "Move", c.File.Path, Destination is null ? "Send to Windows Recycle Bin" : c.Destination,
        Path.GetDirectoryName(c.File.Path)!, "", WorkflowWorkspace.FormatSize(c.File.Length), Bytes: c.File.Length,
        Modified: c.File.LastWrite, Identity: "Reviewed SHA-256: " + c.File.Hash)).ToArray();

    internal string Execute(IOperationProgress progress, CancellationToken ct) => PlanExecution.Transaction(progress, ct, () =>
    {
        if (Destination is not null) { ActionPreset.ValidateFolder(Destination); ArchivePaths.RejectLinks(Destination); }
        // Validate the complete review before any file changes.
        foreach (var change in Changes)
        {
            ct.ThrowIfCancellationRequested(); change.File.Verify(ct);
            if (Destination is not null && (File.Exists(change.Destination) || Directory.Exists(change.Destination)))
                throw new IOException("Reviewed destination is now occupied; review again: " + change.Destination);
        }
        int completed = 0;
        foreach (var change in Changes)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(completed, Changes.Count, (Destination is null ? "Recycling " : "Moving ") + Path.GetFileName(change.File.Path));
            ct.ThrowIfCancellationRequested(); change.File.Verify(ct);
            if (Destination is null) OperationTransaction.Recycle(change.File.Path);
            else
            {
                ArchivePaths.RejectLinks(Destination);
                OperationTransaction.Move(change.File.Path, change.Destination);
            }
            completed++;
        }
        return $"{(Destination is null ? "Sent to Recycle Bin" : "Moved")}: {completed:N0} selected clips."
            + (Destination is null ? "\nRecoverable through Windows Recycle Bin." : "\nDestination: " + Destination);
    });
}
