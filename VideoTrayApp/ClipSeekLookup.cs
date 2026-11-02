namespace VideoTrayApp;

internal sealed record ClipSeekOptions(DateOnly? From = null, DateOnly? Through = null,
    DurationQuery? Duration = null, bool IncludeUnknownDates = false, bool OneRandom = false, decimal? FrameRate = null)
{
    internal void Validate()
    {
        if (From.HasValue != Through.HasValue) throw new ArgumentException("Choose both From and Through dates, or turn off the date filter.");
        if (From > Through) throw new ArgumentException("From date must be on or before Through date.");
        if (FrameRate is <= 0 or > 1000) throw new ArgumentException("Choose All frame rates or a rate greater than zero and no more than 1000 FPS.");
    }
}

internal sealed record SoughtClip(DatedClip File, TimeSpan? Duration, decimal? FrameRate);
internal sealed record ClipSeekResult(IReadOnlyList<SoughtClip> Matches, int Eligible, int Scanned, int Unknown,
    IReadOnlyList<string> Errors, IReadOnlyList<decimal> FrameRates, int UnknownFrameRates);

internal static class ClipSeekLookup
{
    internal static ClipSeekResult Find(string folder, ClipSeekOptions options, bool recursive, TimeZoneInfo timeZone,
        IOperationProgress progress, CancellationToken ct, string? previous = null, Random? random = null)
    {
        options.Validate(); ct.ThrowIfCancellationRequested();
        folder = Path.GetFullPath(folder);
        ActionPreset.ValidateFolder(folder); ArchivePaths.RejectLinks(folder);
        random ??= Random.Shared;
        var errors = new List<string>(); var matches = new List<SoughtClip>();
        var rates = new SortedSet<decimal>(); int unknownRates = 0;
        SoughtClip? chosen = null, previousMatch = null;
        int scanned = 0, unknown = 0, eligible = 0, alternatives = 0;
        foreach (string path in ArchivePaths.Videos([folder], VideoFiles.CreateExtensions(), errors, ct, recursive: recursive))
        {
            ct.ThrowIfCancellationRequested();
            progress.SetIndeterminate($"Seeking clips: {++scanned:N0} MP4s; {Path.GetFileName(path)}");
            try
            {
                using var content = MonthlyBatchPreparer.Open(path, ct);
                var created = ClipDateLookup.ReadCreationTime(content.ReadStream, ct);
                if (created is null) unknown++;
                decimal? rate = Mp4FrameRate.TryRead(content.ReadStream, ct);
                if (rate is { } knownRate) rates.Add(Mp4FrameRate.Rounded(knownRate)); else unknownRates++;
                if (options.FrameRate is { } requested && (rate is null || Mp4FrameRate.Rounded(rate.Value) != Mp4FrameRate.Rounded(requested))) continue;
                if (options.From is { } first && options.Through is { } last)
                {
                    if (created is { } stamp)
                    {
                        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(stamp, timeZone).DateTime);
                        if (date < first || date > last) continue;
                    }
                    else if (!options.IncludeUnknownDates) continue;
                }
                TimeSpan? duration = null;
                if (options.Duration is { } durationQuery)
                {
                    duration = content.Duration();
                    if (!durationQuery.Matches(duration.Value, rate)) continue;
                }
                eligible++;
                var clip = new SoughtClip(new(path, content.Length, created, File.GetLastWriteTimeUtc(path), content.Physical, content.Stamp), duration, rate);
                if (!options.OneRandom) { matches.Add(clip); continue; }
                if (previous is not null && ClipIdentifier.SamePath(path, previous)) { previousMatch = clip; continue; }
                // Reservoir sampling keeps random selection independent of library size and traversal order.
                if (random.Next(++alternatives) == 0) chosen = clip;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add(path + ": " + ex.Message); }
        }
        ct.ThrowIfCancellationRequested();
        if (options.OneRandom && (chosen ?? previousMatch) is { } selected) matches.Add(selected);
        return new(matches.OrderBy(c => c.File.Created ?? DateTimeOffset.MaxValue)
            .ThenBy(c => c.File.Path, StringComparer.OrdinalIgnoreCase).ToArray(), eligible, scanned, unknown, errors, rates.ToArray(), unknownRates);
    }
}
