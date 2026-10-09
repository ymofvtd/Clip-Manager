using System.Text.Json;

namespace VideoTrayApp;

internal enum PresetAction { PrepareBatch, ShuffleAndName, NameByTens, ShuffleRandom, Archive, IdentifyClip, RemoveDuplicates }
internal enum BatchSelection { NumberedMp4, AllVideos, Archives }
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

    internal ActionPreset Clone() => JsonSerializer.Deserialize<ActionPreset>(JsonSerializer.Serialize(this))!;
    internal string ResolveTarget(string workingFolder) => UseWorkingFolder ? workingFolder : Target;
    internal static string Label(PresetAction action) => action switch
    {
        PresetAction.PrepareBatch => "Prepare Batch", PresetAction.ShuffleAndName => "Shuffle & Name",
        PresetAction.NameByTens => "Name by 10s", PresetAction.ShuffleRandom => "Shuffle Random",
        PresetAction.IdentifyClip => "Identify clip", PresetAction.RemoveDuplicates => "Remove duplicates", _ => "Archive"
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
            lines.Add($"Duration: {DurationMinutes} minutes\nSelection: {Selection}\nNaming: {Naming}\nBackup: target's Backup folder");
        }
        if (Action is PresetAction.ShuffleAndName or PresetAction.NameByTens || Action == PresetAction.PrepareBatch && Naming == BatchNaming.NumberByTens)
            lines.Add($"Start: {Start}; padding: {Padding}; prefix: {Prefix}");
        if (Action == PresetAction.ShuffleRandom || Action == PresetAction.PrepareBatch && Naming == BatchNaming.Random)
            lines.Add($"Random name length: {RandomLength}");
        if (Action == PresetAction.Archive) lines.Add("Move all top-level videos, recycle duplicates, shuffle destination filenames.");
        if (Action == PresetAction.IdentifyClip) lines.Add($"Reference: {ReferenceClip}\nMatches: {IdentifyBehavior}; includes subfolders.");
        if (Action == PresetAction.RemoveDuplicates) lines.Add("Includes subfolders. Keep first copy alphabetically; recycle exact duplicates.");
        return string.Join(Environment.NewLine, lines);
    }

    internal void Validate(string workingFolder)
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Enter a preset name.");
        if (!Enum.IsDefined(Action) || !Enum.IsDefined(Selection) || !Enum.IsDefined(Naming) || !Enum.IsDefined(IdentifyBehavior))
            throw new InvalidDataException("Preset contains an unknown action or setting.");
        ValidateFolder(ResolveTarget(workingFolder));
        if (Action is PresetAction.PrepareBatch or PresetAction.Archive || Action == PresetAction.IdentifyClip && IdentifyBehavior == IdentifyBehavior.Move)
        {
            ValidateFolder(Destination);
            if (ClipIdentifier.SamePath(ResolveTarget(workingFolder), Destination) && Action != PresetAction.PrepareBatch)
                throw new IOException("Source and destination must be different folders.");
        }
        if (Action == PresetAction.PrepareBatch) BatchDestinationResolver.ValidateName(BatchFolderName);
        if (DurationMinutes is < 1 or > 10080 || Padding is < 0 or > 64 || RandomLength is < 4 or > 64 || Start < 0)
            throw new ArgumentException("Preset numeric settings are out of range.");
        if (Prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Prefix contains invalid filename characters.");
        if (Action == PresetAction.IdentifyClip && !File.Exists(ReferenceClip))
            throw new FileNotFoundException("The reference clip no longer exists.", ReferenceClip);
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
