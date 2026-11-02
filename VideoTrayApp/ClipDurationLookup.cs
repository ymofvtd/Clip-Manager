using System.Globalization;

namespace VideoTrayApp;

internal enum DurationMatchMode { Exact, WithinFiveSeconds, UpToFiveSecondsShorter, UpToFiveSecondsLonger }

internal sealed record DurationQuery(long Frames, decimal FramesPerSecond, DurationMatchMode Mode,
    bool UseClipFrameRate = false, long WholeSeconds = 0, int FrameField = 0)
{
    internal static DurationQuery Parse(string timecode, decimal fps, DurationMatchMode mode)
    {
        if (fps is < 0.001m or > 1000 || !Enum.IsDefined(mode))
            throw new ArgumentException("Choose a frame rate from 0.001 to 1000 and a duration matching option.");
        string[] parts = timecode.Trim().Split(':');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))
            || !long.TryParse(parts[0], out long minutes) || !int.TryParse(parts[1], out int seconds)
            || !int.TryParse(parts[2], out int frames) || seconds >= 60 || frames >= decimal.Ceiling(fps))
            throw new ArgumentException("Enter MM:SS:FF: minutes, seconds (00–59), and frames below the selected frame rate.");
        decimal count = (minutes * 60m + seconds) * fps + frames;
        if (count <= 0 || count > long.MaxValue || count / fps > (decimal)TimeSpan.MaxValue.TotalSeconds)
            throw new ArgumentException("Enter a positive duration within the supported time range.");
        return new((long)decimal.Round(count, 0, MidpointRounding.AwayFromZero), fps, mode, WholeSeconds: checked(minutes * 60 + seconds), FrameField: frames);
    }

    internal static DurationQuery ParseAutomatic(string timecode, DurationMatchMode mode)
        => Parse(timecode, 1000, mode) with { UseClipFrameRate = true, FramesPerSecond = 0 };

    internal long FrameCount(TimeSpan duration) => checked((long)decimal.Round(
        duration.Ticks * FramesPerSecond / TimeSpan.TicksPerSecond, 0, MidpointRounding.AwayFromZero));

    internal bool Matches(TimeSpan duration, decimal? clipFrameRate = null)
    {
        if (duration <= TimeSpan.Zero) return false;
        if (UseClipFrameRate)
        {
            if (clipFrameRate is > 0)
            {
                decimal rate = clipFrameRate.Value;
                if (FrameField >= decimal.Ceiling(rate)) return false;
                var atRate = this with { UseClipFrameRate = false, FramesPerSecond = rate,
                    Frames = checked((long)decimal.Round(WholeSeconds * rate + FrameField, 0, MidpointRounding.AwayFromZero)) };
                return atRate.Matches(duration);
            }
            // Whole-second queries still work when a clip has no readable FPS. Nonzero FF needs a real or explicit rate.
            if (FrameField != 0) return false;
            decimal seconds = duration.Ticks / (decimal)TimeSpan.TicksPerSecond - WholeSeconds;
            return Mode switch
            {
                DurationMatchMode.Exact => seconds == 0,
                DurationMatchMode.WithinFiveSeconds => Math.Abs(seconds) <= 5,
                DurationMatchMode.UpToFiveSecondsShorter => seconds <= 0 && seconds >= -5,
                DurationMatchMode.UpToFiveSecondsLonger => seconds >= 0 && seconds <= 5,
                _ => false
            };
        }
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

    internal string Format(TimeSpan duration, decimal? clipFrameRate = null)
    {
        if (UseClipFrameRate)
            return clipFrameRate is > 0 ? (this with { UseClipFrameRate = false, FramesPerSecond = clipFrameRate.Value }).Format(duration)
                : $"{(long)duration.TotalMinutes:00}:{duration.Seconds:00}:--";
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
        var scan = ClipSeekLookup.Find(folder, new(Duration: query, OneRandom: true), recursive,
            TimeZoneInfo.Local, progress, ct, previous, random);
        var clip = scan.Matches.FirstOrDefault();
        return new(clip is null ? null : new(clip.File.Path, clip.Duration!.Value, clip.File.Size, clip.File.LastWriteUtc),
            scan.Eligible, scan.Scanned, scan.Errors);
    }
}
