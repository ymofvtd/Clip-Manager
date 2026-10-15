using System.Security.Cryptography;
using System.Text;

namespace VideoTrayApp;

internal sealed record ArchiveProvenance(string Path, string OriginalName, string SourceRoot, string RootLabel,
    DateTimeOffset? SourceCreatedUtc = null, DateTimeOffset? SourceModifiedUtc = null, DateTimeOffset? FirstArchivedUtc = null);
internal sealed record ArchiveFileHint(ClipIdentity Identity, ArchiveFileStamp Stamp);
internal sealed class ArchiveEntry
{
    public ClipIdentity Identity { get; set; } = new(0, TimeSpan.Zero, "");
    public List<string> Locations { get; set; } = [];
    public List<ArchiveProvenance> Sources { get; set; } = [];
    public DateTimeOffset? EmbeddedCreatedUtc { get; set; }
}

internal sealed class ArchiveIndex
{
    public int Version { get; set; } = 1;
    public string Destination { get; set; } = "";
    public List<ArchiveEntry> Entries { get; set; } = [];
    // Only these exact paths may be removed during crash recovery.
    public List<string> OwnedPartials { get; set; } = [];
    private Dictionary<ClipIdentity, ArchiveEntry>? byIdentity;
    public Dictionary<string, ArchiveFileHint> LocationHints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ArchiveFileHint> SourceReceipts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    internal ArchiveEntry? FindEntry(ClipIdentity identity)
    {
        byIdentity ??= Entries.ToDictionary(entry => entry.Identity);
        return byIdentity.GetValueOrDefault(identity);
    }
    internal void InvalidateLocation(string path)
    {
        LocationHints.Remove(path);
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
        // Version 1 indexes without cache fields remain valid and populate them once.
        index.LocationHints = new(index.LocationHints ?? [], StringComparer.OrdinalIgnoreCase);
        index.SourceReceipts = new(index.SourceReceipts ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var hints in new[] { index.LocationHints, index.SourceReceipts })
            foreach (var pair in hints.ToArray())
                if (pair.Value is not { Identity: not null, Stamp: { File: not null, Size: >= 0, Changed: > 0 } } hint
                    || hint.Stamp.Size != hint.Identity.Size || index.FindEntry(hint.Identity) is null)
                    hints.Remove(pair.Key);
        return index;
    }
    internal void Save(string path) => AtomicJson.Save(path, this);

    internal void RecoverPartials(string path)
    {
        if (OwnedPartials.Count == 0) return;
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

    internal bool Refresh(ISet<string> extensions, IoRateLimiter limiter, List<string> errors,
        CancellationToken ct, Action? checkpoint = null, IOperationProgress? progress = null, bool verifyContent = false)
    {
        // Build new locations before replacing the snapshot; cancelled scans keep the old index.
        var locations = new Dictionary<ClipIdentity, List<string>>();
        var refreshedHints = new Dictionary<string, ArchiveFileHint>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in ArchivePaths.Videos([Destination], extensions, errors, ct, checkpoint))
        {
            try
            {
                using var content = new ArchiveContent(file, limiter, ct);
                var stamp = content.Stamp;
                ClipIdentity identity;
                if (!verifyContent && stamp is not null && LocationHints.TryGetValue(file, out var hint) && hint.Stamp == stamp
                    && FindEntry(hint.Identity)?.Locations.Contains(file, StringComparer.OrdinalIgnoreCase) == true)
                    identity = hint.Identity;
                else
                {
                    identity = content.Identity(ct, (done, total) => progress?.Report((int)(done * 100 / Math.Max(1, total)), 100,
                        $"Indexing {file}: {done:N0}/{total:N0} bytes"));
                }
                // Cached candidates require matching protected-file stamps; full rechecks always hash.
                if (stamp is not null) refreshedHints[file] = new(identity, stamp);
                if (!locations.TryGetValue(identity, out var paths)) locations[identity] = paths = [];
                paths.Add(file);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{file}: {ex.Message}"); }
        }
        bool changed = LocationHints.Count != refreshedHints.Count
            || refreshedHints.Any(pair => !LocationHints.TryGetValue(pair.Key, out var hint) || hint != pair.Value)
            || locations.Keys.Any(identity => FindEntry(identity) is null)
            || Entries.Any(entry => locations.TryGetValue(entry.Identity, out var paths)
                ? entry.Locations.Count != paths.Count || !entry.Locations.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(paths)
                : entry.Locations.Count != 0);
        if (!changed) return false;
        foreach (var entry in Entries) entry.Locations = [];
        foreach (var pair in locations)
        {
            var entry = GetEntry(pair.Key);
            entry.Locations = pair.Value;
        }
        LocationHints = refreshedHints;
        return true;
    }
    internal bool IsUnchangedVerifiedSource(string source, IoRateLimiter limiter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!SourceReceipts.TryGetValue(source, out var receipt) || FindEntry(receipt.Identity) is not { } entry) return false;
        try
        {
            using var content = new ArchiveContent(source, limiter, ct);
            if (content.Stamp is not { } sourceStamp || sourceStamp != receipt.Stamp) return false;
            foreach (string location in entry.Locations)
            {
                ct.ThrowIfCancellationRequested();
                if (!ArchivePaths.Contains(Destination, location) || !LocationHints.TryGetValue(location, out var hint)
                    || hint.Identity != receipt.Identity) continue;
                try
                {
                    using var keeper = new ArchiveContent(location, limiter, ct);
                    if (keeper.Stamp is { } stamp && stamp == hint.Stamp) return true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }
    internal void Record(ClipIdentity identity, string location, string source, IEnumerable<string> roots,
        ArchiveFileStamp? sourceStamp = null, ArchiveFileStamp? locationStamp = null, DateTimeOffset? embeddedCreatedUtc = null,
        DateTimeOffset? sourceCreatedUtc = null, DateTimeOffset? sourceModifiedUtc = null)
    {
        var entry = GetEntry(identity);
        entry.EmbeddedCreatedUtc ??= embeddedCreatedUtc;
        if (!entry.Locations.Contains(location, StringComparer.OrdinalIgnoreCase)) entry.Locations.Add(location);
        foreach (string root in roots.Where(root => ArchivePaths.Contains(root, source)))
        {
            int existing = entry.Sources.FindIndex(p => ClipIdentifier.SamePath(p.Path, source) && ClipIdentifier.SamePath(p.SourceRoot, root));
            var origin = existing >= 0 ? entry.Sources[existing]
                : new ArchiveProvenance(source, Path.GetFileName(source), root, Path.GetFileName(Path.TrimEndingDirectorySeparator(root)));
            origin = origin with
            {
                SourceCreatedUtc = origin.SourceCreatedUtc ?? sourceCreatedUtc ?? (sourceStamp is { Created: > 0 } ? DateTimeOffset.FromFileTime(sourceStamp.Created) : null),
                SourceModifiedUtc = origin.SourceModifiedUtc ?? sourceModifiedUtc ?? (sourceStamp is { Written: > 0 } ? DateTimeOffset.FromFileTime(sourceStamp.Written) : null),
                FirstArchivedUtc = origin.FirstArchivedUtc ?? DateTimeOffset.UtcNow
            };
            if (existing >= 0) entry.Sources[existing] = origin; else entry.Sources.Add(origin);
        }
        if (sourceStamp is not null) SourceReceipts[source] = new(identity, sourceStamp);
        else SourceReceipts.Remove(source);
        if (locationStamp is not null) LocationHints[location] = new(identity, locationStamp);
        else LocationHints.Remove(location);
    }

    internal static string OriginDetails(ArchiveEntry entry) =>
        "Embedded MP4 creation (UTC): " + (entry.EmbeddedCreatedUtc?.ToString("O") ?? "Unknown / not captured")
        + string.Concat(entry.Sources.Select(origin => $"\nOriginal name: {origin.OriginalName}\nOriginal path: {origin.Path}\nSource root: {origin.SourceRoot}"
            + $"\nSource filesystem created (UTC, may reflect an earlier copy): {origin.SourceCreatedUtc?.ToString("O") ?? "Unknown"}"
            + $"\nSource filesystem modified (UTC): {origin.SourceModifiedUtc?.ToString("O") ?? "Unknown"}"
            + $"\nFirst cataloged archive verification (UTC): {origin.FirstArchivedUtc?.ToString("O") ?? "Unknown"}"));
}
