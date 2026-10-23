namespace VideoTrayApp;

internal sealed class ArchiveRouteSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Clip archive";
    public ArchiveSettings Settings { get; set; } = new();
    public bool ResumeOnStartup { get; set; }
}

internal sealed class ArchiveRouteStore
{
    public int Version { get; set; } = 1;
    public List<ArchiveRouteSettings> Routes { get; set; } = [];
}

internal sealed class ArchiveRoute(ArchiveRouteSettings configuration, ArchiveWorker worker)
{
    internal ArchiveRouteSettings Configuration { get; set; } = configuration;
    internal ArchiveWorker Worker { get; } = worker;
    public override string ToString() => Configuration.Name;
}

// Routes share foreground priority and destination catalogs, with independent queues and preferences.
internal sealed class ArchiveManager : IDisposable
{
    private readonly OperationCoordinator coordinator;
    private readonly ISet<string> extensions;
    private readonly string dataFolder;
    private readonly List<ArchiveRoute> routes = [];
    internal IReadOnlyList<ArchiveRoute> Routes => routes;
    internal string LoadWarning { get; private set; } = "";
    internal bool Running => routes.Any(route => route.Worker.Running);

    internal ArchiveManager(OperationCoordinator coordinator, ISet<string> extensions, string dataFolder)
    {
        this.coordinator = coordinator; this.extensions = extensions; this.dataFolder = dataFolder;
        try
        {
            var store = AtomicJson.Load<ArchiveRouteStore>(StorePath);
            if (store is not null)
            {
                if (store.Version != 1 || store.Routes is null || store.Routes.Count == 0
                    || store.Routes.Any(route => route is null || route.Id == Guid.Empty || string.IsNullOrWhiteSpace(route.Name) || route.Settings is null)
                    || store.Routes.Select(route => route.Id).Distinct().Count() != store.Routes.Count)
                    throw new InvalidDataException("Unsupported or invalid archive routes; saved settings were retained.");
                foreach (var configuration in store.Routes) routes.Add(Create(configuration));
            }
            else
            {
                var legacy = ArchiveSettings.Load(dataFolder);
                routes.Add(Create(new() { Settings = legacy, ResumeOnStartup = legacy.WatchContinuously }));
                Persist(routes.Select(route => route.Configuration));
            }
        }
        catch (Exception ex) { LoadWarning = "Archive settings need attention: " + ex.Message; }
        if (routes.Count == 0) routes.Add(Create(new()));
    }

    private string StorePath => Path.Combine(dataFolder, "archive_routes.json");
    private ArchiveRoute Create(ArchiveRouteSettings settings) => new(settings,
        new ArchiveWorker(coordinator, extensions, dataFolder, logFileName: $"archive_log_{settings.Id:N}.json"));

    private void Persist(IEnumerable<ArchiveRouteSettings> configurations) =>
        AtomicJson.Save(StorePath, new ArchiveRouteStore { Routes = configurations.ToList() });

    internal ArchiveRoute Add()
    {
        int number = routes.Count + 1;
        while (routes.Any(route => route.Configuration.Name == $"Archive {number}")) number++;
        var added = Create(new() { Name = $"Archive {number}" });
        Persist(routes.Select(route => route.Configuration).Append(added.Configuration));
        routes.Add(added);
        return added;
    }

    internal void Remove(ArchiveRoute route)
    {
        RequireRoute(route);
        if (routes.Count == 1) throw new InvalidOperationException("Keep at least one archive route.");
        if (route.Worker.Running) throw new InvalidOperationException("Stop this route before removing it.");
        Persist(routes.Where(item => item != route).Select(item => item.Configuration));
        routes.Remove(route); route.Worker.Dispose();
    }

    internal void Save(ArchiveRoute route, ArchiveSettings settings, string name) => SaveConfiguration(route, settings, name, resumeOnStartup: false);

    private void SaveConfiguration(ArchiveRoute route, ArchiveSettings settings, string name, bool resumeOnStartup)
    {
        RequireRoute(route);
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Give this archive route a name.");
        if (route.Worker.Running) throw new InvalidOperationException("Stop this route before changing its configuration.");
        settings = settings.Clone();
        // Incomplete new routes can be saved and finished later.
        if (settings.Sources.Count > 0 && !string.IsNullOrWhiteSpace(settings.Destination)) Validate(route, settings);
        var next = new ArchiveRouteSettings { Id = route.Configuration.Id, Name = name, Settings = settings, ResumeOnStartup = resumeOnStartup };
        Persist(routes.Select(item => item == route ? next : item.Configuration));
        route.Configuration = next;
    }

    internal void Start(ArchiveRoute route, ArchiveSettings settings, string name)
    {
        RequireRoute(route);
        if (route.Worker.Running) throw new InvalidOperationException("Stop this route before starting it again.");
        settings = settings.Clone(); Validate(route, settings);
        SaveConfiguration(route, settings, name, resumeOnStartup: settings.WatchContinuously);
        route.Worker.Start(settings);
    }

    private void RequireRoute(ArchiveRoute route)
    {
        if (!routes.Contains(route)) throw new InvalidOperationException("This archive route has been removed.");
    }

    private void Validate(ArchiveRoute route, ArchiveSettings settings)
    {
        settings.Validate(requireAvailable: false);
        foreach (var other in routes.Where(item => item != route))
        {
            var configurations = other.Worker.Running
                ? new[] { other.Configuration.Settings, other.Worker.Settings } : [other.Configuration.Settings];
            foreach (var configuration in configurations)
            {
                if (string.IsNullOrWhiteSpace(configuration.Destination)) continue;
                bool Overlap(string left, string right) => ArchivePaths.Contains(left, right) || ArchivePaths.Contains(right, left);
                if (Overlap(settings.Destination, configuration.Destination))
                    throw new IOException($"Choose a separate destination from '{other}' (destinations cannot overlap).");
                if (settings.Sources.Any(source => Overlap(source, configuration.Destination))
                    || configuration.Sources.Any(source => Overlap(source, settings.Destination)))
                    throw new IOException($"Sources and destinations cannot overlap across archive routes ('{other}').");
            }
        }
    }

    internal string ResumeSaved()
    {
        var errors = new List<string>();
        foreach (var route in routes.Where(route => route.Configuration.ResumeOnStartup && route.Configuration.Settings.WatchContinuously && !route.Worker.Running))
        {
            try { var settings = route.Configuration.Settings.Clone(); Validate(route, settings); route.Worker.Start(settings); }
            catch (Exception ex) { errors.Add($"{route}: {ex.Message}"); }
        }
        return string.Join(Environment.NewLine, errors);
    }

    internal async Task StopAsync(ArchiveRoute route, bool disableResume)
    {
        RequireRoute(route);
        if (disableResume)
        {
            var next = new ArchiveRouteSettings { Id = route.Configuration.Id, Name = route.Configuration.Name, Settings = route.Configuration.Settings.Clone() };
            next.Settings.WatchContinuously = false;
            Persist(routes.Select(item => item == route ? next : item.Configuration));
            route.Configuration = next;
        }
        await route.Worker.StopAsync();
    }

    internal async Task StopAllAsync(bool disableResume = false)
    {
        if (disableResume)
        {
            var next = routes.Select(route => new ArchiveRouteSettings { Id = route.Configuration.Id, Name = route.Configuration.Name,
                Settings = route.Configuration.Settings.Clone() }).ToList();
            foreach (var configuration in next) configuration.Settings.WatchContinuously = false;
            Persist(next);
            for (int i = 0; i < routes.Count; i++) routes[i].Configuration = next[i];
        }
        await Task.WhenAll(routes.Select(route => route.Worker.StopAsync()));
    }

    internal void Pause() { foreach (var route in routes.Where(route => route.Worker.Running)) route.Worker.Pause(); }
    internal void Resume() { foreach (var route in routes.Where(route => route.Worker.Running)) route.Worker.Resume(); }
    internal void RequestReconciliation() { foreach (var route in routes) route.Worker.RequestReconciliation(); }

    internal ArchiveStatus Status
    {
        get
        {
            var values = routes.Select(route => (route, status: route.Worker.Status)).ToArray();
            var active = values.Where(value => value.status.Running).Select(value => value.status).ToArray();
            var representative = active.FirstOrDefault(status => !status.Paused && status.State != ArchiveRuntimeState.Suspended)
                ?? active.FirstOrDefault(status => !status.Paused) ?? active.FirstOrDefault() ?? values[0].status;
            return representative with { Running = active.Length > 0, Paused = active.Length > 0 && active.All(status => status.Paused),
                Queued = values.Sum(value => value.status.Queued), Copied = values.Sum(value => value.status.Copied),
                AlreadyArchived = values.Sum(value => value.status.AlreadyArchived), Failed = values.Sum(value => value.status.Failed),
                Unchanged = values.Sum(value => value.status.Unchanged), LastSuccess = values.Max(value => value.status.LastSuccess),
                Warning = string.Join(Environment.NewLine, values.Where(value => value.status.Warning.Length > 0).Select(value => $"{value.route}: {value.status.Warning}")),
                UnavailableRoots = values.SelectMany(value => value.status.UnavailableRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        }
    }

    public void Dispose() { foreach (var route in routes) route.Worker.Dispose(); }
}
