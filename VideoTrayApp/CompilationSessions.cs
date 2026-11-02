namespace VideoTrayApp;

internal sealed record CompilationSession(Guid Id, string Name, string Folder, string SourceFolder = "")
{
    public override string ToString() => Name + " — " + Folder;
}

internal sealed class CompilationSettings
{
    public int Version { get; set; } = 1;
    public Guid? ActiveSessionId { get; set; }
    public List<CompilationSession> Sessions { get; set; } = [];
}

internal sealed class CompilationSessions
{
    private readonly string settingsPath;
    private CompilationSettings settings;
    internal IReadOnlyList<CompilationSession> Sessions => settings.Sessions;
    internal CompilationSession? Active => Sessions.FirstOrDefault(s => s.Id == settings.ActiveSessionId);
    internal string Warning { get; } = "";
    internal event Action? Changed;

    internal CompilationSessions(string dataFolder)
    {
        settingsPath = Path.Combine(dataFolder, "compilation_sessions.json");
        try
        {
            settings = AtomicJson.Load<CompilationSettings>(settingsPath) ?? new();
            if (settings.Version != 1 || settings.Sessions is null
                || settings.Sessions.Any(s => s is null || s.Id == Guid.Empty || string.IsNullOrWhiteSpace(s.Name)
                    || !Path.IsPathFullyQualified(s.Folder) || s.SourceFolder is null
                    || s.SourceFolder.Length > 0 && !Path.IsPathFullyQualified(s.SourceFolder))
                || settings.Sessions.Select(s => s.Id).Distinct().Count() != settings.Sessions.Count)
                throw new InvalidDataException("Invalid compilation sessions file.");
        }
        catch (Exception ex)
        {
            settings = new();
            Warning = "Compilation sessions could not be loaded. Saved settings were retained: " + ex.Message;
        }
    }

    private void Save(CompilationSettings next)
    {
        if (Warning.Length > 0) throw new IOException(Warning);
        AtomicJson.Save(settingsPath, next); settings = next; Changed?.Invoke();
    }

    internal CompilationSession LockIn(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        ActionPreset.ValidateFolder(folder); ArchivePaths.RejectLinks(folder);
        if (new DirectoryInfo(folder).Parent is null) throw new ArgumentException("Choose a compilation folder, rather than a drive root.");
        if (Sessions.Any(s => ArchivePaths.Contains(s.Folder, folder) || ArchivePaths.Contains(folder, s.Folder)))
            throw new IOException("This folder overlaps an existing compilation session. Select that session or choose a separate folder.");
        var session = new CompilationSession(Guid.NewGuid(), Path.GetFileName(folder), folder);
        Save(new() { Sessions = [.. Sessions, session], ActiveSessionId = session.Id });
        return session;
    }

    internal void Select(Guid id)
    {
        if (!Sessions.Any(s => s.Id == id)) throw new ArgumentException("Choose a saved compilation session.");
        Save(new() { Sessions = [.. Sessions], ActiveSessionId = id });
    }

    internal void Forget(Guid id)
    {
        var remaining = Sessions.Where(s => s.Id != id).ToList();
        Save(new() { Sessions = remaining,
            ActiveSessionId = settings.ActiveSessionId == id ? remaining.FirstOrDefault()?.Id : settings.ActiveSessionId });
    }

    internal void WatchSource(Guid id, string folder)
    {
        var session = Sessions.Single(s => s.Id == id);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            ActionPreset.ValidateFolder(folder); ArchivePaths.RejectLinks(folder);
            if (ArchivePaths.Contains(folder, session.Folder) || ArchivePaths.Contains(session.Folder, folder))
                throw new IOException("Choose a watched source folder separate from the compilation folder.");
        }
        else folder = "";
        Save(new() { Sessions = Sessions.Select(s => s.Id == id ? s with { SourceFolder = folder } : s).ToList(),
            ActiveSessionId = settings.ActiveSessionId });
    }

    internal CompilationSession Rename(Guid id, string name, CancellationToken ct)
    {
        var session = Sessions.Single(s => s.Id == id);
        name = name.Trim(); BatchDestinationResolver.ValidateName(name);
        if (name.Length == 0) throw new ArgumentException("Enter a folder name.");
        ActionPreset.ValidateFolder(session.Folder); ArchivePaths.RejectLinks(session.Folder);
        string parent = Path.GetDirectoryName(session.Folder) ?? throw new IOException("A drive root cannot be renamed.");
        string destination = Path.Combine(parent, name);
        if (string.Equals(session.Folder, destination, StringComparison.Ordinal)) return session;
        if (ClipIdentifier.SamePath(session.Folder, destination))
            throw new ArgumentException("Use a different folder name; changes to capitalization alone are unavailable.");
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("The new folder name is already occupied: " + destination);
        if (Warning.Length > 0) throw new IOException(Warning);
        ct.ThrowIfCancellationRequested();
        Directory.Move(session.Folder, destination);
        var renamed = session with { Name = name, Folder = destination };
        try
        {
            ct.ThrowIfCancellationRequested();
            Save(new() { Sessions = Sessions.Select(s => s.Id == id ? renamed : s).ToList(), ActiveSessionId = settings.ActiveSessionId });
        }
        catch (Exception ex)
        {
            try { Directory.Move(destination, session.Folder); }
            catch (Exception recovery) { throw new IOException(ex.Message + "\nFolder retained at " + destination + "; restoring its old name failed: " + recovery.Message, ex); }
            throw;
        }
        return renamed;
    }
}
