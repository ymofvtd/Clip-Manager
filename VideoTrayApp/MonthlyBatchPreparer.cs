using System.Collections.ObjectModel;
using System.Text.Json;

namespace VideoTrayApp;

internal sealed record MonthlyBatchClip(PlannedFile File, TimeSpan Duration, DateTimeOffset Created, string Name);
internal sealed record MonthlyBatchResult(DateOnly Month, string Directory, int Count, TimeSpan Duration, bool Reused = false)
{
    internal string Summary => $"Best of {Month:MMMM yyyy}: {Count:N0} clips; total duration {WorkflowWorkspace.Duration(Duration)}."
        + (Count == 0 ? "\nNo clips with an embedded creation date in this month; no batch folder created." : "\nBatch: " + Directory)
        + (Reused ? "\nPreviously completed batch verified; no duplicate batch created." : "");
}

internal sealed record MonthlyBatchPlan(string Source, string Output, string Destination, DateOnly Month,
    ReadOnlyCollection<MonthlyBatchClip> Clips, int Scanned, int Unknown, int Duplicates)
{
    internal TimeSpan TotalDuration => TimeSpan.FromTicks(Clips.Sum(c => c.Duration.Ticks));
    internal string Summary => $"Best of {Month:MMMM yyyy}: {Clips.Count:N0} unique clips; total duration {WorkflowWorkspace.Duration(TotalDuration)}."
        + $"\nScanned: {Scanned:N0}; missing embedded creation dates: {Unknown:N0}; duplicate copies excluded: {Duplicates:N0}."
        + "\nBatch: " + Destination;
    internal IReadOnlyList<ContextRow> ReviewRows() => Clips.Select(c => new ContextRow(c.File.Path, "", "Copy",
        c.File.Path, Path.Combine(Destination, c.Name), Path.GetDirectoryName(c.File.Path)!,
        WorkflowWorkspace.Duration(c.Duration), WorkflowWorkspace.FormatSize(c.File.Length), Bytes: c.File.Length,
        Identity: "SHA-256: " + c.File.Hash, Created: c.Created)).ToArray();

    internal MonthlyBatchResult Execute(IOperationProgress progress, CancellationToken ct)
    {
        new MonthlyBatchSettings { ArchiveDirectory = Source, OutputDirectory = Output }.Validate();
        ct.ThrowIfCancellationRequested();
        if (Clips.Count == 0) return new(Month, "", 0, TimeSpan.Zero);
        if (File.Exists(Destination) || Directory.Exists(Destination))
            throw new IOException("The reviewed batch destination is now occupied. Analyze again: " + Destination);

        // Publish the whole verified batch with one directory rename. Only our exact staged files are cleaned up.
        string staging = Path.Combine(Output, "." + Path.GetFileName(Destination) + "." + Guid.NewGuid().ToString("N") + ".pending");
        var owned = new Dictionary<string, PhysicalFileId>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(staging);
        bool published = false;
        try
        {
            int completed = 0;
            foreach (var clip in Clips)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(completed, Clips.Count, "Copying monthly clip: " + Path.GetFileName(clip.File.Path));
                ct.ThrowIfCancellationRequested();
                using var source = MonthlyBatchPreparer.Open(clip.File.Path, ct);
                if (source.Physical != clip.File.Physical || source.Length != clip.File.Length
                    || File.GetLastWriteTimeUtc(clip.File.Path) != clip.File.LastWrite
                    || source.CreationTime(ct) != clip.Created || source.Duration() != clip.Duration)
                    throw new IOException("Reviewed clip changed or was replaced. Analyze again: " + clip.File.Path);
                ArchivePaths.RejectLinks(staging);
                string target = Path.Combine(staging, clip.Name);
                string hash;
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    owned.Add(target, PhysicalFile.Read(output.SafeFileHandle));
                    hash = source.HashTo(output, null, ct);
                    output.Flush(true);
                }
                if (hash != clip.File.Hash) throw new IOException("Reviewed clip content changed. Analyze again: " + clip.File.Path);
                using (var copy = MonthlyBatchPreparer.Open(target, ct))
                    if (copy.Identity(ct) != new ClipIdentity(clip.File.Length, clip.Duration, clip.File.Hash))
                        throw new IOException("Monthly copy verification failed: " + target);
                progress.Report(++completed, Clips.Count, "Verified monthly clip: " + clip.Name);
            }
            ct.ThrowIfCancellationRequested();
            var manifest = new MonthlyBatchManifest { Month = Month, Source = Source,
                Clips = Clips.Select(c => new MonthlyBatchManifestClip(c.Name, c.File.Length, c.Duration.Ticks, c.File.Hash)).ToList() };
            string manifestPath = Path.Combine(staging, MonthlyBatchPreparer.ManifestName);
            using (var file = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owned.Add(manifestPath, PhysicalFile.Read(file.SafeFileHandle));
                JsonSerializer.Serialize(file, manifest); file.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            ArchivePaths.RejectLinks(Output); ArchivePaths.RejectLinks(staging);
            Directory.Move(staging, Destination);
            published = true;
            return new(Month, Destination, Clips.Count, TotalDuration);
        }
        catch (Exception failure)
        {
            var errors = new List<string>();
            foreach (var file in owned)
                try
                {
                    ArchivePaths.RejectLinks(file.Key);
                    if (!File.Exists(file.Key)) continue;
                    if (PhysicalFile.Read(file.Key) != file.Value) throw new IOException("Staged file was replaced; retained.");
                    File.Delete(file.Key);
                }
                catch (Exception ex) { errors.Add(file.Key + ": " + ex.Message); }
            try { ArchivePaths.RejectLinks(staging); Directory.Delete(staging, recursive: false); }
            catch (Exception ex) { errors.Add(staging + ": " + ex.Message); }
            if (errors.Count > 0) throw new IOException(failure.Message + "\nStaging recovery needs attention:\n" + string.Join("\n", errors), failure);
            throw;
        }
        finally
        {
            if (published) progress.SetIndeterminate("Monthly batch ready: " + Destination);
        }
    }
}

internal sealed class MonthlyBatchManifest
{
    public int Version { get; set; } = 1;
    public DateOnly Month { get; set; }
    public string Source { get; set; } = "";
    public List<MonthlyBatchManifestClip> Clips { get; set; } = [];
}
internal sealed record MonthlyBatchManifestClip(string Name, long Size, long DurationTicks, string Hash);

internal static class MonthlyBatchPreparer
{
    internal const string ManifestName = ".best-of-month.json";
    internal static ArchiveContent Open(string path, CancellationToken ct) => new(path, new IoRateLimiter(null, new ArchiveClock()), ct);

    internal static MonthlyBatchPlan Analyze(string source, string output, DateOnly month, IOperationProgress progress,
        CancellationToken ct, bool automatic = false, DateOnly? today = null, TimeZoneInfo? timeZone = null)
    {
        month = MonthlyBatchSettings.MonthStart(month);
        if (month >= MonthlyBatchSettings.MonthStart(today ?? DateOnly.FromDateTime(DateTime.Today)))
            throw new ArgumentException("Choose a completed month. Current and future months are unavailable.");
        var paths = new MonthlyBatchSettings { ArchiveDirectory = source, OutputDirectory = output }; paths.Validate();
        source = paths.ArchiveDirectory; output = paths.OutputDirectory;
        string destination = Path.Combine(output, "best_of_" + month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        string basis = destination;
        for (int suffix = 2; !automatic && (Directory.Exists(destination) || File.Exists(destination)); suffix++) destination = basis + "_" + suffix;
        if (automatic && (Directory.Exists(destination) || File.Exists(destination)))
            throw new IOException("Automatic monthly destination is occupied. Inspect the existing batch: " + destination);
        var zone = timeZone ?? TimeZoneInfo.Local;
        var scan = ClipDateLookup.Find(source, month, month.AddMonths(1).AddDays(-1), true, false, zone, progress, ct);
        if (scan.Errors.Count > 0) throw new IOException("Monthly scan is incomplete; no batch prepared.\n" + string.Join("\n", scan.Errors.Take(20)));
        var clips = new List<MonthlyBatchClip>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        int duplicates = 0;
        foreach (var match in scan.Matches)
        {
            ct.ThrowIfCancellationRequested();
            progress.SetIndeterminate("Analyzing monthly clip: " + Path.GetFileName(match.Path));
            using var content = Open(match.Path, ct);
            var created = content.CreationTime(ct);
            if (created != match.Created || content.Length != match.Size || content.Physical != match.Physical
                || File.GetLastWriteTimeUtc(match.Path) != match.LastWriteUtc)
                throw new IOException("Clip changed during the monthly scan. Analyze again: " + match.Path);
            var identity = content.Identity(ct);
            if (!hashes.Add(identity.Hash)) { duplicates++; continue; }
            var file = new PlannedFile(match.Path, identity.Size, match.LastWriteUtc, content.Physical, identity.Hash);
            clips.Add(new(file, identity.Duration, created!.Value, ""));
        }
        // Match the other batch tools: shortest first, preserving recording order for equal durations.
        clips = clips.OrderBy(c => c.Duration).Select((c, i) => c with { Name = (i + 1).ToString("D4") + Path.GetExtension(c.File.Path) }).ToList();
        return new(source, output, destination, month, clips.AsReadOnly(), scan.Scanned, scan.Unknown, duplicates);
    }

    internal static MonthlyBatchResult PrepareAutomatic(MonthlyBatchSettings settings, DateOnly month,
        IOperationProgress progress, CancellationToken ct, DateOnly? today = null)
    {
        settings.Validate();
        month = MonthlyBatchSettings.MonthStart(month);
        if (month >= MonthlyBatchSettings.MonthStart(today ?? DateOnly.FromDateTime(DateTime.Today)))
            throw new ArgumentException("Automatic preparation requires a completed month.");
        string destination = Path.Combine(settings.OutputDirectory, "best_of_" + month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        if (!Directory.Exists(destination))
            return Analyze(settings.ArchiveDirectory, settings.OutputDirectory, month, progress, ct, automatic: true, today: today).Execute(progress, ct);

        // A crash after publishing but before saving the schedule must not generate a second batch.
        ArchivePaths.RejectLinks(destination);
        string manifestPath = Path.Combine(destination, ManifestName); ArchivePaths.RejectLinks(manifestPath);
        var manifest = AtomicJson.Load<MonthlyBatchManifest>(manifestPath);
        if (manifest is null || manifest.Version != 1 || manifest.Month != month || manifest.Clips is not { Count: > 0 }
            || !ClipIdentifier.SamePath(manifest.Source, settings.ArchiveDirectory))
            throw new IOException("An unrelated or incomplete folder occupies the automatic monthly destination: " + destination);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var clip in manifest.Clips)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(clip.Name) || Path.GetFileName(clip.Name) != clip.Name || !VideoFiles.IsMp4(clip.Name)
                || !names.Add(clip.Name) || clip.DurationTicks <= 0 || clip.Size <= 0)
                throw new InvalidDataException("Invalid monthly batch manifest: " + manifestPath);
            progress.SetIndeterminate("Verifying completed monthly batch: " + clip.Name);
            using var content = Open(Path.Combine(destination, clip.Name), ct);
            if (content.Identity(ct) != new ClipIdentity(clip.Size, TimeSpan.FromTicks(clip.DurationTicks), clip.Hash))
                throw new IOException("A completed monthly clip changed. Inspect: " + destination);
        }
        if (Directory.EnumerateFiles(destination).Count(VideoFiles.IsMp4) != names.Count)
            throw new IOException("The completed monthly batch contains additional MP4 files. Inspect: " + destination);
        return new(month, destination, names.Count, TimeSpan.FromTicks(manifest.Clips.Sum(c => c.DurationTicks)), true);
    }
}
