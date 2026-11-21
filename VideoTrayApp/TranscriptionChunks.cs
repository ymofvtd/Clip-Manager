using System.Text.RegularExpressions;
using Whisper.net;
using static VideoTrayApp.TranscriptionAudio;

namespace VideoTrayApp;

internal readonly record struct SpeechChunk(int Start, int End, int ReadStart, int ReadEnd, int SpeechSamples);

internal static partial class TranscriptionChunks
{
    internal const int WindowSamples = 30 * SampleRate;
    private const int TargetSamples = 25 * SampleRate;
    private const int MinimumChunk = 15 * SampleRate;
    private const int JoinGap = 600 * SampleRate / 1000;
    private const int ShortChunkJoinGap = 1100 * SampleRate / 1000;

    internal static (int Cut, List<SpeechChunk> Chunks) Plan(IReadOnlyList<VadSegmentData> speech,
        int length, int prefix, int padding)
    {
        var ranges = speech.Select(s => (Start: (int)(s.Start.Ticks / TicksPerSample),
            End: (int)(s.End.Ticks / TicksPerSample))).ToArray();
        int cut = length;
        if (length == WindowSamples)
        {
            cut = TargetSamples;
            int nearest = int.MaxValue;
            for (int i = 0; i < ranges.Length; i++)
            {
                int gapStart = ranges[i].End;
                int gapEnd = i + 1 < ranges.Length ? ranges[i + 1].Start : length;
                if (gapEnd - gapStart < SampleRate / 4) continue;
                int boundary = Math.Clamp(TargetSamples, gapStart, gapEnd);
                if (boundary < 20 * SampleRate || boundary > 28 * SampleRate) continue;
                if (Math.Abs(boundary - TargetSamples) >= nearest) continue;
                cut = boundary;
                nearest = Math.Abs(boundary - TargetSamples);
            }
        }

        var chunks = new List<SpeechChunk>();
        foreach (var range in ranges)
        {
            int start = Math.Max(prefix, range.Start);
            int end = Math.Min(cut, range.End);
            if (end <= start) continue;

            int readStart = Math.Max(0, start - padding);
            int readEnd = Math.Min(length, end + padding);
            if (chunks.Count > 0)
            {
                var previous = chunks[^1];
                int joinGap = previous.End - previous.Start < MinimumChunk ? ShortChunkJoinGap : JoinGap;
                if (start - previous.End <= joinGap)
                {
                    chunks[^1] = previous with { End = end, ReadEnd = readEnd, SpeechSamples = previous.SpeechSamples + end - start };
                    continue;
                }
            }
            chunks.Add(new(start, end, readStart, readEnd, end - start));
        }
        return (cut, chunks);
    }

    internal static List<SegmentData> InChunk(IEnumerable<SegmentData> segments, SpeechChunk chunk)
    {
        var start = TimeSpan.FromTicks((chunk.Start - chunk.ReadStart) * TicksPerSample);
        var end = TimeSpan.FromTicks((chunk.End - chunk.ReadStart) * TicksPerSample);
        return segments.Where(s => !string.IsNullOrWhiteSpace(s.Text) && s.End > start && s.Start < end)
            .OrderBy(s => s.Start).ToList();
    }

    internal static string? Review(List<SegmentData> segments, SpeechChunk chunk, int repetitionLimit)
    {
        if (segments.Count == 0) return "no text in the detected speech";
        int removed = segments.RemoveAll(s => IsCaption(s.Text));
        if (segments.Count == 0 || removed > segments.Count) return "non-speech captions dominate the output";
        if (segments.All(s => s.NoSpeechProbability >= 0.65f && s.Probability < 0.48f))
            return "low-confidence text with high no-speech probability";

        int letters = segments.Sum(s => s.Text.Count(char.IsLetterOrDigit));
        double seconds = (chunk.ReadEnd - chunk.ReadStart) / (double)SampleRate;
        if (letters > 26 + 35 * seconds) return "too much text for the audio duration";

        var words = Words().Matches(string.Join(" ", segments.Select(s => s.Text.ToLowerInvariant())))
            .Select(m => m.Value).ToArray();
        // A long, low-confidence loop is suspicious. Short chants and confidently decoded repeats are common on Twitch.
        if (words.Length >= 12 && segments.Average(s => s.Probability) < 0.65f && HasLoop(words, repetitionLimit))
            return "the same words repeat in a low-confidence loop";
        return null;
    }

    private static bool HasLoop(string[] words, int limit)
    {
        for (int size = 1; size <= Math.Min(8, words.Length / limit); size++)
        {
            int matching = 0;
            for (int i = size; i < words.Length; i++)
            {
                matching = words[i] == words[i - size] ? matching + 1 : 0;
                if (matching >= size * (limit - 1) && matching + size >= words.Length * 0.5)
                    return true;
            }
        }
        return false;
    }

    internal static bool IsCaption(string text)
    {
        string remaining = MusicCaptions().Replace(text, "").Trim(' ', '\t', '\r', '\n', '.', '!', '♪', '♫', '♬');
        return remaining.Length == 0;
    }

    internal static (string Text, int Removed) TrimOverlap(string previous, string current)
    {
        var left = Tokens().Matches(previous);
        var right = Tokens().Matches(current);
        for (int count = Math.Min(8, Math.Min(left.Count, right.Count)); count > 0; count--)
        {
            bool same = true;
            for (int i = 0; i < count && same; i++)
            {
                string a = left[left.Count - count + i].Value.TrimEnd('.', ',', '!', '?', '…');
                string b = right[i].Value.TrimEnd('.', ',', '!', '?', '…');
                same = a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            if (same)
                return (current[(right[count - 1].Index + right[count - 1].Length)..].TrimStart(), count);
        }
        return (current, 0);
    }

    [GeneratedRegex(@"[\[\(（【]\s*(?:background\s+music|music|musik|musique|música|musica|音楽|音乐|音樂|음악|applause|silence|inaudible|unintelligible)[^\p{L}\p{N}]*?[\]\)）】]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MusicCaptions();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();

    [GeneratedRegex(@"\S+")]
    private static partial Regex Tokens();
}
