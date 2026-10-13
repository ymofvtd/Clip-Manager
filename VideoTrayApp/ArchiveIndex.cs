using System.Security.Cryptography;
using System.Text;

namespace VideoTrayApp;

internal sealed record ArchiveProvenance(string Path, string OriginalName, string SourceRoot, string RootLabel);
internal sealed class ArchiveEntry
{
    public ClipIdentity Identity { get; set; } = new(0, TimeSpan.Zero, "");
    public List<string> Locations { get; set; } = [];
    public List<ArchiveProvenance> Sources { get; set; } = [];
}

internal sealed class ArchiveIndex
{
    public int Version { get; set; } = 1;
    public string Destination { get; set; } = "";
    public List<ArchiveEntry> Entries { get; set; } = [];
    // Only these exact paths may be removed during crash recovery.
    public List<string> OwnedPartials { get; set; } = [];
    private Dictionary<ClipIdentity, ArchiveEntry>? byIdentity;
    private Dictionary<string, LocationHint> locationHints = new(StringComparer.OrdinalIgnoreCase);
    internal ArchiveEntry? FindEntry(ClipIdentity identity)
    {
        byIdentity ??= Entries.ToDictionary(entry => entry.Identity);
        return byIdentity.GetValueOrDefault(identity);
    }
    internal void InvalidateLocation(string path)
    {
        locationHints.Remove(path);
        foreach (var entry in Entries) entry.Locations.RemoveAll(location => ClipIdentifier.SamePath(location, path));
    }
    private ArchiveEntry GetEntry(ClipIdentity identity)
    {
        var entry = FindEntry(identity);
        if (entry is null)
        {
            entry = new ArchiveEntry { Identity = identity };
            Entries.Add(entry); byIdentity!.Add(identity, entry);
        }
        return entry;
    }

    internal static string IndexPath(string dataFolder, string destination) => Path.Combine(dataFolder,
        "archive_index_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)).ToUpperInvariant())))[..24] + ".json");
    internal static ArchiveIndex Load(string path, string destination)
    {
        var index = AtomicJson.Load<ArchiveIndex>(path) ?? new ArchiveIndex { Destination = destination };
        if (index.Version != 1 || !ClipIdentifier.SamePath(index.Destination, destination)
            || index.Entries is null || index.OwnedPartials is null
            || index.Entries.Any(e => e.Identity is null || e.Locations is null || e.Sources is null)
            || index.Entries.Select(e => e.Identity).Distinct().Count() != index.Entries.Count)
            throw new InvalidDataException("Unsupported or invalid archive index; existing files were left untouched.");
        return index;
    }
    internal void Save(string path) => AtomicJson.Save(path, this);

    internal void RecoverPartials(string path)
    {
        foreach (string partial in OwnedPartials.ToArray())
        {
            if (!ClipIdentifier.SamePath(Path.GetDirectoryName(partial)!, Destination)
                || !Path.GetFileName(partial).StartsWith(".clipsmanager-", StringComparison.Ordinal)
                || !partial.EndsWith(".partial", StringComparison.Ordinal))
                throw new InvalidDataException("Unsafe temporary path in archive index.");
            ArchivePaths.RejectLinks(partial);
            if (File.Exists(partial)) File.Delete(partial);
            OwnedPartials.Remove(partial);
        }
        Save(path);
    }

    internal void Refresh(ISet<string> extensions, IoRateLimiter limiter, List<string> errors,
        CancellationToken ct, Action? checkpoint = null, IOperationProgress? progress = null)
    {
        // Build new locations before replacing the snapshot; cancelled scans keep the old index.
        var locations = new Dictionary<ClipIdentity, List<string>>();
        var refreshedHints = new Dictionary<string, LocationHint>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in ArchivePaths.Videos([Destination], extensions, errors, ct, checkpoint))
        {
            try
            {
                var info = new FileInfo(file);
                ClipIdentity identity;
                if (locationHints.TryGetValue(file, out var hint) && hint.Size == info.Length && hint.Time == info.LastWriteTimeUtc)
                    identity = hint.Identity;
                else
                {
                    using var content = new ArchiveContent(file, limiter, ct);
                    identity = content.Identity(ct, (done, total) => progress?.Report((int)(done * 100 / Math.Max(1, total)), 100,
                        $"Indexing {file}: {done:N0}/{total:N0} bytes"));
                }
                // Metadata can only retain a candidate location. Process always verifies the actual content again.
                refreshedHints[file] = new(identity, info.Length, info.LastWriteTimeUtc);
                if (!locations.TryGetValue(identity, out var paths)) locations[identity] = paths = [];
                paths.Add(file);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{file}: {ex.Message}"); }
        }
        foreach (var entry in Entries) entry.Locations = [];
        foreach (var pair in locations)
        {
            var entry = GetEntry(pair.Key);
            entry.Locations = pair.Value;
        }
        locationHints = refreshedHints;
    }
    internal void Record(ClipIdentity identity, string location, string source, IEnumerable<string> roots)
    {
        var entry = GetEntry(identity);
        if (!entry.Locations.Contains(location, StringComparer.OrdinalIgnoreCase)) entry.Locations.Add(location);
        foreach (string root in roots.Where(root => ArchivePaths.Contains(root, source)))
            if (!entry.Sources.Any(p => ClipIdentifier.SamePath(p.Path, source) && ClipIdentifier.SamePath(p.SourceRoot, root)))
                entry.Sources.Add(new(source, Path.GetFileName(source), root, Path.GetFileName(Path.TrimEndingDirectorySeparator(root))));
    }
    private sealed record LocationHint(ClipIdentity Identity, long Size, DateTime Time);
}
