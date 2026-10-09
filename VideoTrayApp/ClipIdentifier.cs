using System.Security.Cryptography;
using Microsoft.VisualBasic.FileIO;

namespace VideoTrayApp;

internal sealed record ClipIdentity(long Size, TimeSpan Duration, string Hash);
internal sealed record ClipSearchResult(ClipIdentity Reference, List<string> Matches, List<string> Errors);

internal static class ClipIdentifier
{
    internal static ClipIdentity ReadIdentity(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Keep writers out while reading both metadata and the full content hash.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var video = TagLib.File.Create(path);
        var duration = video.Properties.Duration;
        if (duration <= TimeSpan.Zero)
            throw new InvalidDataException("Cannot read a valid video duration.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        ct.ThrowIfCancellationRequested();
        return new ClipIdentity(stream.Length, duration, Convert.ToHexString(hash.GetHashAndReset()));
    }

    internal static ClipSearchResult Find(string referencePath, string folder,
        ISet<string> extensions, IOperationProgress progress, CancellationToken ct)
    {
        progress.SetIndeterminate("Reading reference clip hash and duration...");
        var reference = ReadIdentity(referencePath, ct);
        var matches = new List<string>();
        var errors = new List<string>();
        var folders = new Stack<string>();
        folders.Push(folder);
        int scanned = 0;
        while (folders.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string current = folders.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(current);
                foreach (string child in Directory.GetDirectories(current))
                {
                    ct.ThrowIfCancellationRequested();
                    // Avoid junction loops and searching outside the selected tree.
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        folders.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{current}: {ex.Message}");
                continue;
            }
            foreach (string path in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!extensions.Contains(Path.GetExtension(path)) || SamePath(path, referencePath))
                    continue;
                progress.SetIndeterminate($"Scanned {++scanned} clips: {Path.GetFileName(path)}");
                try
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != reference.Size)
                        continue;
                    // Duration and size are cheap prefilters; only identical content is a match.
                    using (var video = TagLib.File.Create(path))
                    {
                        if (video.Properties.Duration != reference.Duration)
                            continue;
                    }
                    if (ReadIdentity(path, ct) == reference)
                        matches.Add(path);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors.Add($"{path}: {ex.Message}");
                }
            }
        }
        matches.Sort(StringComparer.OrdinalIgnoreCase);
        return new ClipSearchResult(reference, matches, errors);
    }

    internal static string Apply(ClipSearchResult result, string referencePath, string? destination,
        IOperationProgress progress, CancellationToken ct)
    {
        int completed = 0, skipped = 0;
        var errors = new List<string>();
        bool cancelled = false;
        for (int i = 0; i < result.Matches.Count; i++)
        {
            string path = result.Matches[i];
            try
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(i, result.Matches.Count, $"Verifying {Path.GetFileName(path)}...");
                if (SamePath(path, referencePath)
                    || (destination is not null && SamePath(Path.GetDirectoryName(path)!, destination)))
                {
                    skipped++;
                    continue;
                }
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                    || ReadIdentity(path, ct) != result.Reference)
                {
                    errors.Add($"{path}: Clip changed since the search; left untouched.");
                    continue;
                }
                ct.ThrowIfCancellationRequested();
                if (destination is null)
                {
                    OperationTransaction.Recycle(path);
                }
                else
                {
                    OperationTransaction.Move(path, UniqueDestination(destination, Path.GetFileName(path)));
                }
                completed++;
                progress.Report(i + 1, result.Matches.Count, $"Processed {Path.GetFileName(path)}");
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
            }
        }
        if (OperationTransaction.IsActive)
        {
            ct.ThrowIfCancellationRequested();
            if (errors.Count > 0) throw new IOException("Identify clip failed." + FormatErrors(errors));
        }
        string verb = destination is null ? "Sent to Recycle Bin" : "Moved";
        string summary = $"{(cancelled ? "Operation cancelled." : "Operation completed.")}\n\n" +
            $"{verb}: {completed}\nSkipped (already in destination): {skipped}\nErrors: {errors.Count}";
        if (cancelled)
            summary += $"\nUnprocessed: {result.Matches.Count - completed - skipped - errors.Count}";
        return summary + FormatErrors(errors);
    }

    internal static bool SamePath(string first, string second) =>
        string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string UniqueDestination(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        int suffix = 1;
        while (File.Exists(path) || Directory.Exists(path))
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)}__{suffix++}{Path.GetExtension(name)}");
        return path;
    }

    internal static string FormatErrors(List<string> errors) => errors.Count == 0 ? string.Empty :
        "\n\n" + string.Join("\n", errors.Take(5)) + (errors.Count > 5 ? "\nMore errors omitted." : string.Empty);
}
