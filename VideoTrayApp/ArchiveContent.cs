using System.Security.Cryptography;

namespace VideoTrayApp;

// TagLib uses the same protected handle. Its metadata reads share the content I/O budget.
internal sealed class ArchiveContent : TagLib.File.IFileAbstraction, IDisposable
{
    private readonly FileStream file;
    private readonly BudgetStream stream;
    public string Name { get; }
    public Stream ReadStream => stream;
    public Stream WriteStream => throw new NotSupportedException();
    public void CloseStream(Stream ignored) { }
    internal long Length => file.Length;
    internal ArchiveFileStamp? Stamp => PhysicalFile.ArchiveStamp(file.SafeFileHandle, Length);
    internal ArchiveContent(string path, IoRateLimiter limiter, CancellationToken ct, string? formatName = null, bool allowRename = false)
    {
        ArchivePaths.RejectLinks(path);
        Name = formatName ?? path;
        file = new FileStream(path, FileMode.Open, FileAccess.Read, allowRename ? FileShare.Read | FileShare.Delete : FileShare.Read, 64 * 1024);
        stream = new BudgetStream(file, limiter, ct);
    }
    internal TimeSpan Duration()
    {
        stream.Position = 0;
        using var video = TagLib.File.Create(this);
        TimeSpan duration = video.Properties.Duration;
        if (duration <= TimeSpan.Zero) throw new InvalidDataException("Cannot read a valid video duration.");
        return duration;
    }
    internal ClipIdentity Identity(CancellationToken ct, Action<long, long>? progress = null)
    {
        TimeSpan duration = Duration();
        return new(Length, duration, HashTo(null, progress, ct));
    }
    internal string HashTo(Stream? output, Action<long, long>? progress, CancellationToken ct, IoRateLimiter? writeBudget = null,
        string? protectedHash = null)
    {
        stream.Position = 0;
        // A hash established on this still-protected handle can be reused during copying.
        using var hash = protectedHash is null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var buffer = new byte[64 * 1024];
        long done = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash?.AppendData(buffer, 0, read);
            if (output is not null) { writeBudget?.Charge(read, ct); output.Write(buffer, 0, read); }
            progress?.Invoke(done += read, Length);
        }
        ct.ThrowIfCancellationRequested();
        return protectedHash ?? Convert.ToHexString(hash!.GetHashAndReset());
    }
    public void Dispose() => file.Dispose();

    private sealed class BudgetStream(Stream inner, IoRateLimiter limiter, CancellationToken ct) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            // Limit even a library-requested large read to cancellable chunks.
            int total = 0;
            while (total < count)
            {
                int requested = Math.Min(64 * 1024, count - total);
                limiter.Charge((int)Math.Min(requested, Math.Max(0, inner.Length - inner.Position)), ct);
                int read = inner.Read(buffer, offset + total, requested);
                total += read;
                if (read < requested) break;
            }
            return total;
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
