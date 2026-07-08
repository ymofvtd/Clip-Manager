namespace VideoTrayApp
{
    public sealed record VideoFingerprint(long Size, TimeSpan Duration);

    public sealed class VideoFingerprintComparer : IEqualityComparer<VideoFingerprint>
    {
        public static VideoFingerprintComparer Instance { get; } = new();

        public bool Equals(VideoFingerprint? x, VideoFingerprint? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return x.Size == y.Size && x.Duration == y.Duration;
        }

        public int GetHashCode(VideoFingerprint obj)
        {
            return HashCode.Combine(obj.Size, obj.Duration);
        }
    }

    public static class VideoFingerprintFactory
    {
        public static VideoFingerprint Create(string path)
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
                throw new FileNotFoundException("Video file not found.", path);

            TimeSpan duration;
            using (var video = TagLib.File.Create(path))
            {
                duration = video.Properties.Duration;
            }

            return new VideoFingerprint(fileInfo.Length, duration);
        }

        public static bool TryCreate(string path, out VideoFingerprint? fingerprint, out string? error)
        {
            fingerprint = null;
            error = null;

            try
            {
                fingerprint = Create(path);
                return true;
            }
            catch (IOException ex)
            {
                error = ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}