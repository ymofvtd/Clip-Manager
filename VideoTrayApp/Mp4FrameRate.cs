using System.Buffers.Binary;

namespace VideoTrayApp;

internal sealed record FrameRateDiscovery(IReadOnlyList<decimal> Rates, int Scanned, int Unknown, IReadOnlyList<string> Errors);

internal static class Mp4FrameRate
{
    // Average video FPS from mdhd's time scale and stts's sample counts/durations; never round to an integer.
    // https://developer.apple.com/documentation/quicktime-file-format/time-to-sample_atom/time-to-sample_table
    internal static decimal? Read(Stream stream, CancellationToken ct)
    {
        stream.Position = 0;
        var movieEnd = ClipDateLookup.FindAtom(stream, stream.Length, 0x6D6F6F76, ct); // moov
        if (movieEnd is null) return null;
        while (stream.Position < movieEnd)
        {
            var trackEnd = ClipDateLookup.FindAtom(stream, movieEnd.Value, 0x7472616B, ct); // trak
            if (trackEnd is null) return null;
            long trackStart = stream.Position;
            var rate = ReadTrack(stream, trackStart, trackEnd.Value, ct);
            if (rate is > 0) return rate;
            stream.Position = trackEnd.Value;
        }
        return null;
    }

    private static decimal? ReadTrack(Stream stream, long start, long end, CancellationToken ct)
    {
        stream.Position = start;
        var mediaEnd = ClipDateLookup.FindAtom(stream, end, 0x6D646961, ct); // mdia
        if (mediaEnd is null) return null;
        long mediaStart = stream.Position;
        var handlerEnd = ClipDateLookup.FindAtom(stream, mediaEnd.Value, 0x68646C72, ct); // hdlr
        if (handlerEnd is null || handlerEnd.Value - stream.Position < 12) return null;
        Span<byte> bytes = stackalloc byte[12]; stream.ReadExactly(bytes);
        if (BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 0x76696465) return null; // vide, excluding audio and metadata tracks
        stream.Position = mediaStart;
        var headerEnd = ClipDateLookup.FindAtom(stream, mediaEnd.Value, 0x6D646864, ct); // mdhd
        if (headerEnd is null || stream.Position == headerEnd) return null;
        long headerStart = stream.Position;
        int version = stream.ReadByte();
        int offset = version switch { 0 => 12, 1 => 20, _ => -1 };
        if (offset < 0 || headerEnd.Value - headerStart < offset + 4) return null;
        stream.Position = headerStart + offset; stream.ReadExactly(bytes[..4]);
        uint scale = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (scale == 0) return null;
        stream.Position = mediaStart;
        var infoEnd = ClipDateLookup.FindAtom(stream, mediaEnd.Value, 0x6D696E66, ct); // minf
        if (infoEnd is null) return null;
        var tableEnd = ClipDateLookup.FindAtom(stream, infoEnd.Value, 0x7374626C, ct); // stbl
        if (tableEnd is null) return null;
        var timesEnd = ClipDateLookup.FindAtom(stream, tableEnd.Value, 0x73747473, ct); // stts
        if (timesEnd is null || timesEnd.Value - stream.Position < 8) return null;
        stream.ReadExactly(bytes[..8]);
        if (bytes[0] != 0) return null;
        uint entries = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
        if ((ulong)entries * 8 > (ulong)(timesEnd.Value - stream.Position)) throw new InvalidDataException("Truncated MP4 sample timing table.");
        decimal samples = 0, ticks = 0;
        for (uint i = 0; i < entries; i++)
        {
            ct.ThrowIfCancellationRequested(); stream.ReadExactly(bytes[..8]);
            uint count = BinaryPrimitives.ReadUInt32BigEndian(bytes), delta = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
            if (count > 0 && delta == 0) return null;
            samples += count; ticks += (decimal)count * delta;
        }
        return ticks > 0 ? samples * scale / ticks : null;
    }

    internal static decimal? TryRead(Stream stream, CancellationToken ct)
    {
        try { return Read(stream, ct); }
        catch (InvalidDataException) { return null; }
    }

    internal static decimal Rounded(decimal rate) => decimal.Round(rate, 3, MidpointRounding.AwayFromZero);

    internal static FrameRateDiscovery Discover(string folder, bool recursive, IOperationProgress progress, CancellationToken ct)
    {
        folder = Path.GetFullPath(folder); ActionPreset.ValidateFolder(folder); ArchivePaths.RejectLinks(folder);
        var errors = new List<string>(); var rates = new SortedSet<decimal>();
        int scanned = 0, unknown = 0;
        foreach (string path in ArchivePaths.Videos([folder], VideoFiles.CreateExtensions(), errors, ct, recursive: recursive))
        {
            ct.ThrowIfCancellationRequested(); progress.SetIndeterminate($"Detecting frame rates: {++scanned:N0} clips; {Path.GetFileName(path)}");
            try
            {
                using var content = MonthlyBatchPreparer.Open(path, ct);
                if (TryRead(content.ReadStream, ct) is { } rate) rates.Add(Rounded(rate)); else unknown++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add(path + ": " + ex.Message); }
        }
        ct.ThrowIfCancellationRequested();
        return new(rates.ToArray(), scanned, unknown, errors);
    }
}
