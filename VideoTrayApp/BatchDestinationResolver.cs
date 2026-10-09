using System.Numerics;

namespace VideoTrayApp;

internal sealed record BatchDestination(string Path, bool Create)
{
    internal void Validate(string source, string parent)
    {
        ActionPreset.ValidateFolder(parent);
        if (ClipIdentifier.SamePath(source, Path))
            throw new IOException("Batch folder must be different from the source folder.");
        string backup = System.IO.Path.Combine(System.IO.Path.GetFullPath(source), "Backup");
        if (ClipIdentifier.SamePath(backup, Path)
            || System.IO.Path.GetFullPath(Path).StartsWith(backup + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The batch folder must be outside the source's Backup folder.");
        if (Create)
        {
            if (Directory.Exists(Path) || File.Exists(Path))
                throw new IOException($"The planned batch folder now exists. Run again to choose a fresh folder: {Path}");
        }
        else ActionPreset.ValidateFolder(Path);
    }
}

internal static class BatchDestinationResolver
{
    internal static BatchDestination Resolve(ActionPreset preset, string source)
    {
        ActionPreset.ValidateFolder(preset.Destination);
        ValidateName(preset.BatchFolderName);
        string parent = Path.GetFullPath(preset.Destination);
        var current = Directory.EnumerateDirectories(parent)
            .Where(path => Path.GetFileName(path).StartsWith("c_", StringComparison.OrdinalIgnoreCase)
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(path => Number(Path.GetFileName(path)[2..]))
            .ThenByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (current is not null)
        {
            var existing = new BatchDestination(current, false);
            existing.Validate(source, parent);
            return existing;
        }

        string name = preset.BatchFolderName.Trim();
        string candidate = Path.Combine(parent, string.IsNullOrEmpty(name) ? "batch_1" : name);
        for (long suffix = string.IsNullOrEmpty(name) ? 2 : 1; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
            candidate = Path.Combine(parent, string.IsNullOrEmpty(name) ? $"batch_{suffix}" : $"{name}_{suffix}");
        var created = new BatchDestination(candidate, true);
        created.Validate(source, parent);
        return created;
    }

    internal static void ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        string stem = name.Split('.')[0];
        if (name is "." or ".." || name.EndsWith('.') || name.Length > 200
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Enter a valid batch folder name without a path or reserved Windows name.");
    }

    private static BigInteger Number(string suffix) => suffix.Length > 0 && suffix.All(char.IsAsciiDigit)
        && BigInteger.TryParse(suffix, out var number) ? number : -1;
}
