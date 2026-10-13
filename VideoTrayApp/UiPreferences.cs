using System.Text.Json;

namespace VideoTrayApp;

internal enum AppTheme { System, Light, Dark }

internal sealed class UiPreferences
{
    public int SchemaVersion { get; set; } = 1;
    public AppTheme Theme { get; set; }
    public bool LaunchIntoTray { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool Notifications { get; set; }
    public bool SetupDismissed { get; set; }
    public Rectangle Bounds { get; set; }
    public bool Maximized { get; set; }
    public string Destination { get; set; } = "Overview";
    public List<string> RecentPaths { get; set; } = [];
    public Dictionary<string, int[]> TableWidths { get; set; } = [];
    private bool preserveCorruptFile;

    internal static UiPreferences Load(string folder, out string warning)
    {
        warning = "";
        string path = Path.Combine(folder, "ui_settings.json");
        try
        {
            if (!File.Exists(path)) return new();
            var value = JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(path));
            if (value is null || value.SchemaVersion != 1 || !Enum.IsDefined(value.Theme)
                || value.RecentPaths is null || value.TableWidths is null || string.IsNullOrWhiteSpace(value.Destination))
                throw new InvalidDataException("Unsupported or invalid UI preferences.");
            value.RecentPaths = value.RecentPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Take(12).ToList();
            return value;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            warning = $"UI preferences could not be read. Defaults are in use; the original file is retained. {ex.Message}";
            return new() { preserveCorruptFile = true };
        }
    }

    internal void Save(string folder)
    {
        string path = Path.Combine(folder, "ui_settings.json");
        if (preserveCorruptFile) { LocalJson.Backup(path); preserveCorruptFile = false; }
        LocalJson.Save(path, this);
    }

    internal static Rectangle ClampBounds(Rectangle saved, Rectangle workArea)
    {
        int width = Math.Min(Math.Max(saved.Width, 640), workArea.Width);
        int height = Math.Min(Math.Max(saved.Height, 480), workArea.Height);
        return new Rectangle(Math.Clamp(saved.X, workArea.Left, workArea.Right - width),
            Math.Clamp(saved.Y, workArea.Top, workArea.Bottom - height), width, height);
    }
}

internal static class LocalJson
{
    internal static void Backup(string path)
    { if (File.Exists(path)) File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json"); }
    internal static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}

internal enum JobState { Editing, Analyzing, ReadyToReview, Running, Cancelling, Completed, CompletedWithIssues, Cancelled, Failed, Interrupted }
internal sealed record JobRecord(string Action, string Context, DateTimeOffset Started, DateTimeOffset? Finished,
    JobState State, string Details);

internal sealed class ActivityHistory
{
    internal const int Capacity = 100;
    private readonly object gate = new();
    private readonly List<JobRecord> records = [];
    internal IReadOnlyList<JobRecord> Entries { get { lock (gate) return records.ToArray(); } }
    internal string Warning { get; private set; } = "";
    private bool preserveCorruptFile;
    private sealed record HistoryDocument(int SchemaVersion, List<JobRecord> Entries);
    internal ActivityHistory(string folder)
    {
        try
        {
            string path = Path.Combine(folder, "activity.json");
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path);
                var document = text.TrimStart().StartsWith('[') ? new HistoryDocument(1, JsonSerializer.Deserialize<List<JobRecord>>(text) ?? [])
                    : JsonSerializer.Deserialize<HistoryDocument>(text) ?? throw new InvalidDataException("Empty activity history.");
                if (document.SchemaVersion != 1 || document.Entries is null || document.Entries.Any(job => job is null || job.Action is null || job.Context is null || job.Details is null || !Enum.IsDefined(job.State)))
                    throw new InvalidDataException("Invalid or unsupported activity history.");
                records.AddRange(document.Entries.TakeLast(Capacity).Select(job => job.Finished is null ? job with
                { State = JobState.Interrupted, Details = job.Details + "\nApplication stopped before a final outcome was recorded. Inspect affected folders and recovery files." } : job));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { Warning = "Activity history could not be loaded; original file retained. " + ex.Message; preserveCorruptFile = true; }
    }
    internal void Add(JobRecord entry)
    {
        lock (gate)
        {
            records.Add(entry);
            if (records.Count > Capacity) records.RemoveRange(0, records.Count - Capacity);
        }
    }
    internal void Replace(JobRecord original, JobRecord outcome)
    { lock (gate) { int index = records.IndexOf(original); if (index >= 0) records[index] = outcome; } }
    internal void Save(string folder)
    {
        lock (gate)
        {
            string path = Path.Combine(folder, "activity.json");
            if (preserveCorruptFile) { LocalJson.Backup(path); preserveCorruptFile = false; }
            LocalJson.Save(path, new HistoryDocument(1, records.ToList()));
        }
    }
}
