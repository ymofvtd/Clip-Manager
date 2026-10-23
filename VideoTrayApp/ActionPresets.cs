using System.Text.Json;

namespace VideoTrayApp;

internal enum PresetAction { PrepareBatch, ShuffleAndName, NameByTens, ShuffleRandom, Archive, IdentifyClip, RemoveDuplicates, ShuffleBatches, SeekDuplications }
internal enum BatchSelection { NumberedMp4, AllVideos, Archives }
// Retained for loading saved presets; Prepare Batch always numbers clips by duration.
internal enum BatchNaming { Preserve, NumberByTens, Random }
internal enum IdentifyBehavior { Move, Recycle }

internal sealed class ActionPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New preset";
    public PresetAction Action { get; set; }
    public string Target { get; set; } = "";
    public bool UseWorkingFolder { get; set; } = true;
    public string Destination { get; set; } = "";
    public string BatchFolderName { get; set; } = "";
    public int DurationMinutes { get; set; } = 20;
    public BatchSelection Selection { get; set; }
    public BatchNaming Naming { get; set; }
    public int Start { get; set; }
    public int Padding { get; set; }
    public string Prefix { get; set; } = "";
    public int RandomLength { get; set; } = 12;
    public string ReferenceClip { get; set; } = "";
    public IdentifyBehavior IdentifyBehavior { get; set; }
    public List<string> ArchiveSources { get; set; } = [];
    public ArchiveSpeed ArchiveSpeed { get; set; } = ArchiveSpeed.Slow;
    public int ArchiveIntervalSeconds { get; set; } = 120;
    public int? ArchiveMegabytesPerSecond { get; set; } = 20;
    public int ArchiveSettlingSeconds { get; set; } = 10;
    public string DirectoryB { get; set; } = "";
    public bool IncludeSubfolders { get; set; } = true;

    internal ArchiveSettings ArchiveConfiguration(string workingFolder) => new()
    {
        Sources = UseWorkingFolder ? new[] { workingFolder }.Concat(ArchiveSources).ToList()
            : ArchiveSources.Count > 0 ? ArchiveSources.ToList() : [Target],
        Destination = Destination, Speed = ArchiveSpeed, IntervalSeconds = ArchiveIntervalSeconds,
        MegabytesPerSecond = ArchiveMegabytesPerSecond, SettlingSeconds = ArchiveSettlingSeconds,
        WatchContinuously = false
    };

    internal ActionPreset Clone() => JsonSerializer.Deserialize<ActionPreset>(JsonSerializer.Serialize(this))!;
    internal string ResolveTarget(string workingFolder) => UseWorkingFolder ? workingFolder : Target;
    internal static string Label(PresetAction action) => action switch
    {
        PresetAction.PrepareBatch => "Prepare Batch", PresetAction.ShuffleAndName => "Shuffle & Name",
        PresetAction.NameByTens => "Name by 10s", PresetAction.ShuffleRandom => "Shuffle Random",
        PresetAction.IdentifyClip => "Identify clip", PresetAction.RemoveDuplicates => "Remove duplicates",
        PresetAction.ShuffleBatches => "Shuffle Batches", PresetAction.SeekDuplications => "Seek duplications", _ => "Archive"
    };

    internal string Summary(string workingFolder)
    {
        var lines = new List<string> { Name, $"Action: {Label(Action)}", $"Target: {ResolveTarget(workingFolder)}" };
        if (Action is PresetAction.PrepareBatch or PresetAction.Archive || Action == PresetAction.IdentifyClip && IdentifyBehavior == IdentifyBehavior.Move)
            lines.Add($"Destination: {Destination}" + (Action == PresetAction.PrepareBatch ? " (batch parent folder)" : ""));
        if (Action == PresetAction.PrepareBatch)
        {
            lines.Add("Use the highest numbered c_ folder, or create a new batch if none exists.");
            lines.Add($"New folder name: {(string.IsNullOrWhiteSpace(BatchFolderName) ? "batch_1, batch_2, ... (next available)" : BatchFolderName)}");
            lines.Add($"Duration: {DurationMinutes} minutes\nSelection: {Selection}\nNaming: 1, 2, 3, ... shortest to longest (new clips only; occupied numbers skipped)\nBackup: target's Backup folder");
        }
        if (Action is PresetAction.ShuffleAndName or PresetAction.NameByTens)
            lines.Add($"Start: {Start}; padding: {Padding}; prefix: {Prefix}");
        if (Action == PresetAction.ShuffleRandom)
            lines.Add($"Random name length: {RandomLength}");
        if (Action == PresetAction.Archive)
        {
            var settings = ArchiveConfiguration(workingFolder);
            lines.Add($"Sources (direct files only): {string.Join("; ", settings.Sources)}\nCopy and verify only; sources unchanged; completed copies retained on cancellation.\nRun once, {ArchiveSpeed}; interval {ArchiveIntervalSeconds}s; I/O cap {ArchiveMegabytesPerSecond?.ToString() ?? "off"} MB/s; settle {ArchiveSettlingSeconds}s.");
        }
        if (Action == PresetAction.SeekDuplications) lines.Add($"Directory B: {DirectoryB}\nInclude subfolders: {IncludeSubfolders}\nScan exact copies; always review individual paths and confirm selection. All start as Keep.");
        if (Action == PresetAction.IdentifyClip) lines.Add($"Reference: {ReferenceClip}\nMatches: {IdentifyBehavior}; includes subfolders.");
        if (Action == PresetAction.RemoveDuplicates) lines.Add("Includes subfolders. Keep first copy alphabetically; recycle exact duplicates.");
        if (Action == PresetAction.ShuffleBatches)
            lines.Add($"Mix all archive folders and loose root clips into batches up to {DurationMinutes} minutes.\nIncludes subfolders; excludes Backup and links.\nLonger clips get their own folders. Existing folders reused; new batch folders added as needed.\nNumbered filenames from shortest to longest within each batch. No backups or duplicate deletion; cancellation restores original layout.");
        return string.Join(Environment.NewLine, lines);
    }

    internal void Validate(string workingFolder)
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Enter a preset name.");
        if (!Enum.IsDefined(Action) || !Enum.IsDefined(Selection) || !Enum.IsDefined(Naming) || !Enum.IsDefined(IdentifyBehavior))
            throw new InvalidDataException("Preset contains an unknown action or setting.");
        if (Action == PresetAction.Archive) ArchiveConfiguration(workingFolder).Validate();
        else ValidateFolder(ResolveTarget(workingFolder));
        if (Action == PresetAction.SeekDuplications) ValidateFolder(DirectoryB);
        if (Action is PresetAction.PrepareBatch or PresetAction.Archive || Action == PresetAction.IdentifyClip && IdentifyBehavior == IdentifyBehavior.Move)
        {
            ValidateFolder(Destination);
            if (Action != PresetAction.Archive && ClipIdentifier.SamePath(ResolveTarget(workingFolder), Destination) && Action != PresetAction.PrepareBatch)
                throw new IOException("Source and destination must be different folders.");
        }
        if (Action == PresetAction.PrepareBatch) BatchDestinationResolver.ValidateName(BatchFolderName);
        if (DurationMinutes is < 1 or > 10080 || Padding is < 0 or > 64 || RandomLength is < 4 or > 64 || Start < 0)
            throw new ArgumentException("Preset numeric settings are out of range.");
        if (Prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Prefix contains invalid filename characters.");
        if (Action == PresetAction.IdentifyClip) VideoFiles.RequireMp4(ReferenceClip);
        if (Action == PresetAction.IdentifyClip && !File.Exists(ReferenceClip))
            throw new FileNotFoundException("The reference clip no longer exists.", ReferenceClip);
    }

    // Editing a preset must not discard useful settings when a removable drive is absent.
    internal void ValidateForSave()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Enter a preset name.");
        if (!Enum.IsDefined(Action) || !Enum.IsDefined(Selection) || !Enum.IsDefined(IdentifyBehavior) || !Enum.IsDefined(ArchiveSpeed))
            throw new InvalidDataException("Preset contains an unknown action or setting.");
        if (DurationMinutes is < 1 or > 10080 || Padding is < 0 or > 64 || RandomLength is < 4 or > 64 || Start < 0)
            throw new ArgumentException("Preset numeric settings are out of range.");
        if (Prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Invalid filename prefix.");
        BatchDestinationResolver.ValidateName(BatchFolderName);
        if (Action == PresetAction.IdentifyClip) VideoFiles.RequireMp4(ReferenceClip);
        foreach (string path in new[] { Target, Destination, ReferenceClip, DirectoryB }.Concat(ArchiveSources))
            if (path.Length > 0 && !Path.IsPathFullyQualified(path)) throw new ArgumentException("Use a full path: " + path);
        if (ArchiveIntervalSeconds is < 0 or > 86400 || ArchiveSettlingSeconds is < 0 or > 3600 || ArchiveMegabytesPerSecond is < 1 or > 10000)
            throw new ArgumentException("Archive scheduling settings are out of range.");
    }

    internal static void ValidateFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Folder no longer exists: {folder}");
        var current = new DirectoryInfo(Path.GetFullPath(folder));
        for (; current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Choose a regular folder rather than a link or junction: {folder}");
    }
}

internal sealed class PresetStore
{
    public List<ActionPreset> Presets { get; set; } = [];
    // A missing entry means Always ask.
    public Dictionary<PresetAction, string> Defaults { get; set; } = [];
    internal ActionPreset? DefaultFor(PresetAction action) => Defaults.TryGetValue(action, out var id)
        ? Presets.FirstOrDefault(p => p.Id == id && p.Action == action) : null;
    internal static PresetStore Load(string path)
    {
        if (!File.Exists(path)) return new();
        var store = JsonSerializer.Deserialize<PresetStore>(File.ReadAllText(path)) ?? throw new InvalidDataException("Preset file is empty.");
        if (store.Presets is null || store.Defaults is null || store.Presets.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id))
            || store.Presets.Select(p => p.Id).Distinct().Count() != store.Presets.Count
            || store.Defaults.Any(entry => !store.Presets.Any(p => p.Id == entry.Value && p.Action == entry.Key)))
            throw new InvalidDataException("Preset file contains missing or invalid preset references.");
        if (store.Presets.Any(p => !Enum.IsDefined(p.Action) || !Enum.IsDefined(p.Selection) || !Enum.IsDefined(p.Naming)
            || !Enum.IsDefined(p.IdentifyBehavior) || !Enum.IsDefined(p.ArchiveSpeed)
            || p.Name is null || p.Target is null || p.Destination is null || p.BatchFolderName is null
            || p.Prefix is null || p.ReferenceClip is null || p.DirectoryB is null || p.ArchiveSources is null
            || p.ArchiveSources.Any(source => source is null)))
            throw new InvalidDataException("Preset file contains an unknown action or missing field. The original file has been retained; repair it before saving.");
        return store;
    }
    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
