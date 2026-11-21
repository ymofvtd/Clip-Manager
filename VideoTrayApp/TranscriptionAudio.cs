using System.Buffers.Binary;
using NAudio.Wave;

namespace VideoTrayApp;

internal sealed class TranscriptionAudio : IDisposable
{
    internal const int SampleRate = 16_000;
    internal const long TicksPerSample = TimeSpan.TicksPerSecond / SampleRate;
    private readonly MediaFoundationReader reader;
    private readonly MediaFoundationResampler resampler;
    private readonly ISampleProvider samples;
    private bool formatChanged;

    internal TranscriptionAudio(string path, Stream input, Action<string> log, CancellationToken ct)
    {
        reader = new MediaFoundationReader(path, new() { RepositionInRead = false });
        try
        {
            var (editDelay, audioEnd) = ReadTrackTiming(input, ct);
            AudioEnd = audioEnd;
            log($"Media duration {reader.TotalTime:c}; declared audio end {AudioEnd?.ToString("c") ?? "unknown"}; decode format {reader.WaveFormat}");
            reader.Position = 0;
            StartSample = Math.Max(reader.CurrentTime.Ticks, editDelay.Ticks) / TicksPerSample;
            var decodeFormat = reader.WaveFormat;
            reader.WaveFormatChanged += (_, _) => formatChanged |= !decodeFormat.Equals(reader.WaveFormat);
            resampler = new MediaFoundationResampler(reader, new WaveFormat(SampleRate, 16, 1));
            samples = resampler.ToSampleProvider();
            log($"Resampled {resampler.WaveFormat}; Whisper input {samples.WaveFormat}; "
                + $"first audio at {TimeSpan.FromTicks(StartSample * TicksPerSample):c}");
        }
        catch
        {
            resampler?.Dispose();
            reader.Dispose();
            throw;
        }
    }

    internal TimeSpan MediaDuration => reader.TotalTime;
    internal TimeSpan? AudioEnd { get; }
    internal long StartSample { get; }
    internal long SamplesRead { get; private set; }

    internal int Read(Span<float> destination, CancellationToken ct)
    {
        int total = 0;
        while (total < destination.Length)
        {
            ct.ThrowIfCancellationRequested();
            int read = samples.Read(destination.Slice(total, Math.Min(SampleRate, destination.Length - total)));
            if (formatChanged) throw new InvalidDataException("The decoded audio format changed inside the MP4 track.");
            if (read == 0) break;
            total += read;
            SamplesRead += read;
        }
        return total;
    }

    internal void Validate()
    {
        if (SamplesRead < SampleRate / 20)
            throw new InvalidDataException("The decoded audio track is empty or shorter than 50 ms.");
        var expected = AudioEnd ?? MediaDuration;
        double missing = expected.TotalSeconds - (StartSample + SamplesRead) / (double)SampleRate;
        double tolerance = Math.Clamp(expected.TotalSeconds * 0.02, 1, 5);
        if (missing > tolerance)
            throw new InvalidDataException($"The decoded audio ended {missing:F1} seconds before the expected end of the audio track.");
    }

    private static (TimeSpan Delay, TimeSpan? End) ReadTrackTiming(Stream input, CancellationToken ct)
    {
        // tkhd measures the track on the movie timeline, including a leading empty edit.
        input.Position = 0;
        if (ClipDateLookup.FindAtom(input, input.Length, 0x6D6F6F76, ct) is not { } movieEnd) return default; // moov
        long movieStart = input.Position;
        if (ClipDateLookup.FindAtom(input, movieEnd, 0x6D766864, ct) is not { } headerEnd) return default; // mvhd
        Span<byte> bytes = stackalloc byte[8];
        if (!ReadField(input, headerEnd, 12, 20, bytes[..4], bytes[..4])) return default;
        uint scale = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (scale == 0) return default;

        input.Position = movieStart;
        while (ClipDateLookup.FindAtom(input, movieEnd, 0x7472616B, ct) is { } trackEnd) // trak
        {
            long trackStart = input.Position;
            if (ClipDateLookup.FindAtom(input, trackEnd, 0x6D646961, ct) is { } mediaEnd && // mdia
                ClipDateLookup.FindAtom(input, mediaEnd, 0x68646C72, ct) is { } handlerEnd && handlerEnd - input.Position >= 12) // hdlr
            {
                input.Position += 8;
                input.ReadExactly(bytes[..4]);
                if (BinaryPrimitives.ReadUInt32BigEndian(bytes) == 0x736F756E) // soun
                {
                    input.Position = trackStart;
                    if (ClipDateLookup.FindAtom(input, trackEnd, 0x746B6864, ct) is not { } trackHeaderEnd) return default; // tkhd
                    bytes.Clear();
                    if (!ReadField(input, trackHeaderEnd, 20, 28, bytes[4..], bytes)) return default;
                    ulong duration = BinaryPrimitives.ReadUInt64BigEndian(bytes);
                    double seconds = duration / (double)scale;
                    if (duration is 0 or uint.MaxValue or ulong.MaxValue || seconds >= TimeSpan.MaxValue.TotalSeconds) return default;
                    input.Position = trackStart;
                    return (ReadLeadingEdit(input, trackEnd, scale, duration, ct), TimeSpan.FromSeconds(seconds));
                }
            }
            input.Position = trackEnd;
        }
        return default;
    }

    private static TimeSpan ReadLeadingEdit(Stream input, long trackEnd, uint scale, ulong duration, CancellationToken ct)
    {
        if (ClipDateLookup.FindAtom(input, trackEnd, 0x65647473, ct) is not { } editsEnd || // edts
            ClipDateLookup.FindAtom(input, editsEnd, 0x656C7374, ct) is not { } end || end - input.Position < 8) // elst
            return TimeSpan.Zero;
        Span<byte> bytes = stackalloc byte[16];
        input.ReadExactly(bytes[..8]);
        int size = bytes[0] switch { 0 => 4, 1 => 8, _ => 0 };
        uint count = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
        int entrySize = size * 2 + 4;
        if (size == 0 || (ulong)count * (uint)entrySize > (ulong)(end - input.Position))
            return TimeSpan.Zero;
        ulong delay = 0;
        for (uint i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            input.ReadExactly(bytes[..(size * 2)]);
            input.Position += 4; // media rate
            long mediaStart = size == 4 ? BinaryPrimitives.ReadInt32BigEndian(bytes[4..]) : BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
            if (mediaStart != -1) break;
            ulong emptyDuration = size == 4 ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes);
            if (emptyDuration >= duration - delay) return TimeSpan.Zero;
            delay += emptyDuration;
        }
        return TimeSpan.FromSeconds(delay / (double)scale);
    }

    private static bool ReadField(Stream input, long end, int offsetV0, int offsetV1, Span<byte> fieldV0, Span<byte> fieldV1)
    {
        long start = input.Position;
        if (start == end) return false;
        int version = input.ReadByte();
        int offset = version == 1 ? offsetV1 : offsetV0;
        Span<byte> field = version == 1 ? fieldV1 : fieldV0;
        if (version is not (0 or 1) || end - start < offset + field.Length) return false;
        input.Position = start + offset;
        input.ReadExactly(field);
        return true;
    }

    public void Dispose()
    {
        resampler.Dispose();
        reader.Dispose();
    }
}
