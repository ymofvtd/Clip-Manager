using System.Globalization;

namespace VideoTrayApp;

internal enum DurationMatchMode { Exact, WithinFiveSeconds, UpToFiveSecondsShorter, UpToFiveSecondsLonger }

internal sealed record DurationQuery(long Frames, decimal FramesPerSecond, DurationMatchMode Mode)
{
    internal static DurationQuery Parse(string timecode, decimal fps, DurationMatchMode mode)
    {
        if (fps is < 1 or > 240 || !Enum.IsDefined(mode))
            throw new ArgumentException("Choose a frame rate from 1 to 240 and a duration matching option.");
        string[] parts = timecode.Trim().Split(':');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))
            || !long.TryParse(parts[0], out long minutes) || !int.TryParse(parts[1], out int seconds)
            || !int.TryParse(parts[2], out int frames) || seconds >= 60 || frames >= decimal.Ceiling(fps))
            throw new ArgumentException("Enter MM:SS:FF: minutes, seconds (00–59), and frames below the selected frame rate.");
        decimal count = (minutes * 60m + seconds) * fps + frames;
        if (count <= 0 || count > long.MaxValue || count / fps > (decimal)TimeSpan.MaxValue.TotalSeconds)
            throw new ArgumentException("Enter a positive duration within the supported time range.");
        return new((long)decimal.Round(count, 0, MidpointRounding.AwayFromZero), fps, mode);
    }

    internal long FrameCount(TimeSpan duration) => checked((long)decimal.Round(
        duration.Ticks * FramesPerSecond / TimeSpan.TicksPerSecond, 0, MidpointRounding.AwayFromZero));

    internal bool Matches(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return false;
        decimal difference = (decimal)FrameCount(duration) - Frames;
        return Mode switch
        {
            DurationMatchMode.Exact => difference == 0,
            DurationMatchMode.WithinFiveSeconds => Math.Abs(difference) <= 5 * FramesPerSecond,
            DurationMatchMode.UpToFiveSecondsShorter => difference <= 0 && difference >= -5 * FramesPerSecond,
            DurationMatchMode.UpToFiveSecondsLonger => difference >= 0 && difference <= 5 * FramesPerSecond,
            _ => false
        };
    }

    internal string Format(TimeSpan duration)
    {
        // Display elapsed minutes/seconds plus frames, rounding consistently with exact matching.
        long wholeSeconds = (long)decimal.Floor(duration.Ticks / (decimal)TimeSpan.TicksPerSecond);
        int frames = (int)decimal.Round((duration.Ticks % TimeSpan.TicksPerSecond) * FramesPerSecond / TimeSpan.TicksPerSecond,
            0, MidpointRounding.AwayFromZero);
        if (frames >= decimal.Ceiling(FramesPerSecond)) { wholeSeconds++; frames = 0; }
        return string.Create(CultureInfo.InvariantCulture, $"{wholeSeconds / 60:00}:{wholeSeconds % 60:00}:{frames:00}");
    }
}

internal sealed record DurationClip(string Path, TimeSpan Duration, long Size, DateTime LastWriteUtc);
internal sealed record ClipDurationResult(DurationClip? Clip, int Matches, int Scanned, IReadOnlyList<string> Errors);

internal static class ClipDurationLookup
{
    internal static ClipDurationResult Find(string folder, DurationQuery query, bool recursive,
        IOperationProgress progress, CancellationToken ct, string? previous = null, Random? random = null)
    {
        folder = Path.GetFullPath(folder);
        ActionPreset.ValidateFolder(folder); ArchivePaths.RejectLinks(folder);
        random ??= Random.Shared;
        var errors = new List<string>();
        DurationClip? chosen = null, previousMatch = null;
        int matches = 0, scanned = 0, alternatives = 0;
        foreach (string path in ArchivePaths.Videos([folder], VideoFiles.CreateExtensions(), errors, ct, recursive: recursive))
        {
            ct.ThrowIfCancellationRequested();
            progress.SetIndeterminate($"Reading durations: {++scanned:N0} clips; {Path.GetFileName(path)}");
            try
            {
                using var content = MonthlyBatchPreparer.Open(path, ct);
                TimeSpan duration = content.Duration();
                if (!query.Matches(duration)) continue;
                matches++;
                var clip = new DurationClip(path, duration, content.Length, File.GetLastWriteTimeUtc(path));
                if (previous is not null && ClipIdentifier.SamePath(previous, path)) { previousMatch = clip; continue; }
                // Reservoir sampling gives every eligible clip the same chance without retaining the entire library.
                if (random.Next(++alternatives) == 0) chosen = clip;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add(path + ": " + ex.Message); }
        }
        ct.ThrowIfCancellationRequested();
        return new(chosen ?? previousMatch, matches, scanned, errors);
    }
}
