namespace VideoTrayApp;

internal sealed class MonthlyBatchSettings
{
    public bool Enabled { get; set; }
    public bool NotifyOnCompletion { get; set; } = true;
    public string ArchiveDirectory { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public DateOnly? NextMonth { get; set; }

    internal static DateOnly MonthStart(DateOnly date) => new(date.Year, date.Month, 1);
    internal DateOnly? DueMonth(DateOnly today)
    {
        var current = MonthStart(today);
        var next = NextMonth ?? current.AddMonths(-1);
        return Enabled && next < current ? next : null;
    }

    internal void Validate(bool requireAvailable = true)
    {
        if (string.IsNullOrWhiteSpace(ArchiveDirectory) || string.IsNullOrWhiteSpace(OutputDirectory))
            throw new ArgumentException("Choose an archive directory and an output directory.");
        ArchiveDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ArchiveDirectory));
        OutputDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(OutputDirectory));
        if (ArchivePaths.Contains(ArchiveDirectory, OutputDirectory) || ArchivePaths.Contains(OutputDirectory, ArchiveDirectory))
            throw new IOException("The archive and monthly output directories must not contain one another.");
        foreach (string folder in new[] { ArchiveDirectory, OutputDirectory })
        {
            ArchivePaths.RejectLinks(folder);
            if (requireAvailable) ActionPreset.ValidateFolder(folder);
        }
        if (NextMonth is { Day: not 1 }) throw new ArgumentException("The next automatic month must start on day 1.");
    }

    internal static MonthlyBatchSettings Load(string folder)
    {
        var settings = AtomicJson.Load<MonthlyBatchSettings>(Path.Combine(folder, "monthly_batch_settings.json")) ?? new();
        if (settings.ArchiveDirectory is null || settings.OutputDirectory is null || settings.NextMonth is { Day: not 1 })
            throw new InvalidDataException("Invalid monthly preparation settings.");
        return settings;
    }
    internal void Save(string folder) => AtomicJson.Save(Path.Combine(folder, "monthly_batch_settings.json"), this);
}
