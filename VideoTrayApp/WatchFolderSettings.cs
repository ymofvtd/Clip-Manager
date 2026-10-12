using System.Text.Json;

namespace VideoTrayApp;

internal static class WatchFolderSettings
{
    public static List<string> Normalize(IEnumerable<string> paths) => paths
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static List<string> Load(string appDataFolder, string legacyAppDataFolder)
    {
        string path = Path.Combine(appDataFolder, "watch_folders.json");
        if (File.Exists(path))
            return Normalize(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? []);

        // Migrate the single-folder setting without changing the working folder.
        foreach (string folder in new[] { appDataFolder, legacyAppDataFolder })
        {
            string oldPath = Path.Combine(folder, "config.txt");
            if (!File.Exists(oldPath)) continue;
            var paths = Normalize([File.ReadAllText(oldPath)]);
            Save(appDataFolder, paths);
            return paths;
        }
        return [];
    }

    public static void Save(string appDataFolder, IEnumerable<string> paths)
    {
        Directory.CreateDirectory(appDataFolder);
        string path = Path.Combine(appDataFolder, "watch_folders.json");
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Normalize(paths),
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
