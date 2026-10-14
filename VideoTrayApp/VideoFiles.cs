namespace VideoTrayApp;

internal static class VideoFiles
{
    internal const string Extension = ".mp4";
    internal const string PickerFilter = "MP4 videos (*.mp4)|*.mp4";

    internal static HashSet<string> CreateExtensions() => new(StringComparer.OrdinalIgnoreCase) { Extension };

    internal static bool IsMp4(string path) =>
        string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    internal static void RequireMp4(string path)
    {
        if (!IsMp4(path)) throw new ArgumentException("Choose an .mp4 clip.", nameof(path));
    }
}
