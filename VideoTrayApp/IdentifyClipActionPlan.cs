namespace VideoTrayApp;

internal sealed record IdentifyClipScan(PlannedFile Reference, IReadOnlyList<PlannedFile> Matches)
{
    internal IdentifyClipActionPlan Prepare(ISet<string> selected, string? destination, IOperationProgress progress, CancellationToken ct)
    {
        Reference.Verify(ct);
        if (destination is not null)
        {
            destination = Path.GetFullPath(destination);
            ActionPreset.ValidateFolder(destination); ArchivePaths.RejectLinks(destination);
        }
        var changes = new List<PlannedChange>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Matches.Where(file => selected.Contains(file.Path)))
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(changes.Count, selected.Count, "Preparing selected match: " + Path.GetFileName(file.Path));
            file.Verify(ct);
            if (file.Physical == Reference.Physical || ClipIdentifier.SamePath(file.Path, Reference.Path))
                throw new IOException("The reference clip and its aliases cannot be changed: " + file.Path);
            string target = "";
            if (destination is not null)
            {
                if (ClipIdentifier.SamePath(Path.GetDirectoryName(file.Path)!, destination))
                    throw new IOException("A selected clip is already in the destination. Choose another folder or deselect it: " + file.Path);
                target = Path.Combine(destination, Path.GetFileName(file.Path));
                for (int suffix = 1; File.Exists(target) || Directory.Exists(target) || reserved.Contains(target); suffix++)
                {
                    ct.ThrowIfCancellationRequested();
                    target = Path.Combine(destination, Path.GetFileNameWithoutExtension(file.Path) + "__" + suffix + Path.GetExtension(file.Path));
                }
                reserved.Add(target);
            }
            changes.Add(new(file, target));
        }
        if (changes.Count == 0) throw new ArgumentException("Select at least one identified clip.");
        // Share the same verified move/recycle transaction used by Find by date.
        return new(Reference, new ClipDateActionPlan(changes.AsReadOnly(), destination));
    }
}

internal sealed record IdentifyClipActionPlan(PlannedFile Reference, ClipDateActionPlan Selection)
{
    internal IReadOnlyList<ContextRow> ReviewRows() => Selection.ReviewRows();

    internal string Execute(IOperationProgress progress, CancellationToken ct)
    {
        // Keep the reference protected from writes/deletion throughout the transaction.
        using var kept = new FileStream(Reference.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Reference.Verify(ct);
        return Selection.Execute(progress, ct) + "\nReference preserved: " + Reference.Path;
    }
}
