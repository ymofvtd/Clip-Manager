using System.Buffers.Binary;

namespace VideoTrayApp;

internal sealed record DatedClip(string Path, long Size, DateTimeOffset? Created);
internal sealed record ClipDateResult(IReadOnlyList<DatedClip> Matches, int Scanned, int Unknown,
    IReadOnlyList<string> Errors);

internal static class ClipDateLookup
{
    private static readonly DateTimeOffset Epoch = new(1904, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Read moov/mvhd only, seeking past media payloads rather than reading whole videos.
    // The embedded movie creation time survives byte-for-byte copies and renames.
    internal static DateTimeOffset? ReadCreationTime(string path, CancellationToken ct)
    {
        VideoFiles.RequireMp4(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadCreationTime(stream, ct);
    }

    internal static DateTimeOffset? ReadCreationTime(Stream stream, CancellationToken ct)
    {
        stream.Position = 0;
        var movie = FindAtom(stream, stream.Length, 0x6D6F6F76, ct); // moov
        if (movie is null) throw new InvalidDataException("Missing MP4 movie metadata.");
        var header = FindAtom(stream, movie.Value, 0x6D766864, ct); // mvhd
        if (header is null) return null;
        long remaining = header.Value - stream.Position;
        if (remaining < 4) throw new InvalidDataException("Truncated MP4 movie header.");
        Span<byte> data = stackalloc byte[12];
        stream.ReadExactly(data[..4]);
        int version = data[0];
        if (version is not (0 or 1)) return null;
        if (remaining < (version == 0 ? 100 : 112))
            throw new InvalidDataException("Truncated MP4 movie header.");
        stream.ReadExactly(data[..(version == 0 ? 4 : 8)]);
        ulong seconds = version == 0 ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt64BigEndian(data);
        if (seconds == 0 || seconds == (version == 0 ? uint.MaxValue : ulong.MaxValue)
            || seconds > (ulong)((DateTimeOffset.MaxValue - Epoch).Ticks / TimeSpan.TicksPerSecond)) return null;
        return Epoch.AddTicks((long)seconds * TimeSpan.TicksPerSecond);
    }

    // Returns the atom's end, leaving the stream at its payload. Every atom stays in its parent.
    private static long? FindAtom(Stream stream, long end, uint type, CancellationToken ct)
    {
        Span<byte> header = stackalloc byte[8];
        while (stream.Position < end)
        {
            ct.ThrowIfCancellationRequested();
            long start = stream.Position;
            if (end - start < 8) throw new InvalidDataException("Truncated MP4 atom.");
            stream.ReadExactly(header);
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
            uint atomType = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            int headerSize = 8;
            if (size == 1)
            {
                if (end - stream.Position < 8) throw new InvalidDataException("Truncated extended MP4 atom.");
                stream.ReadExactly(header);
                size = BinaryPrimitives.ReadUInt64BigEndian(header);
                headerSize = 16;
            }
            else if (size == 0) size = (ulong)(end - start);
            if (size < (ulong)headerSize || size > (ulong)(end - start))
                throw new InvalidDataException("Invalid MP4 atom size.");
            long atomEnd = start + (long)size;
            if (atomType == type) return atomEnd;
            stream.Position = atomEnd;
        }
        return null;
    }

    internal static ClipDateResult Find(string folder, DateOnly from, DateOnly through, bool recursive,
        bool includeUnknown, TimeZoneInfo timeZone, IOperationProgress progress, CancellationToken ct)
    {
        if (from > through) throw new ArgumentException("From date must be on or before Through date.");
        folder = Path.GetFullPath(folder);
        ActionPreset.ValidateFolder(folder);
        var matches = new List<DatedClip>();
        var errors = new List<string>();
        var folders = new Stack<string>();
        folders.Push(folder);
        int scanned = 0, unknown = 0;
        while (folders.TryPop(out string? current))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Reject linked ancestors as well as links encountered during traversal.
                for (var parent = new DirectoryInfo(current); parent is not null; parent = parent.Parent)
                    if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Linked folders and their descendants are excluded.");
                foreach (string path in Directory.EnumerateFiles(current))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!VideoFiles.IsMp4(path)) continue;
                    try
                    {
                        var info = new FileInfo(path);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        progress.SetIndeterminate($"Reading creation dates: {++scanned:N0} clips; {info.Name}");
                        var created = ReadCreationTime(path, ct);
                        if (created is null) unknown++;
                        bool inRange = created is { } stamp
                            && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(stamp, timeZone).DateTime) is var date
                            && date >= from && date <= through;
                        if (inRange || created is null && includeUnknown) matches.Add(new(path, info.Length, created));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    { errors.Add(path + ": " + ex.Message); }
                }
                if (recursive)
                    foreach (string child in Directory.EnumerateDirectories(current))
                    {
                        ct.ThrowIfCancellationRequested();
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) folders.Push(child);
                    }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { errors.Add(current + ": " + ex.Message); }
        }
        return new(matches.OrderBy(c => c.Created ?? DateTimeOffset.MaxValue)
            .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToArray(), scanned, unknown, errors);
    }
}
