using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace VideoTrayApp
{
    public sealed class UpdateCheckResult
    {
        public required Version CurrentVersion { get; init; }
        public Version? LatestVersion { get; init; }
        public string? DownloadUrl { get; init; }
        public bool UpdateAvailable { get; init; }
    }

    public static class AppUpdater
    {
        private const string RepoOwner = "ymofvtd";
        private const string RepoName = "Clip-Manager";
        private const int AssetRetryCount = 5;
        private const int AssetRetryDelayMs = 2000;

        // API client: GitHub JSON only.
        private static readonly HttpClient ApiHttp = CreateApiClient();

        // Download client: no GitHub JSON Accept header (avoids odd CDN/API negotiation).
        private static readonly HttpClient DownloadHttp = CreateDownloadClient();

        private static HttpClient CreateApiClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ClipsManager-Updater");
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        private static HttpClient CreateDownloadClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ClipsManager-Updater");
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            return client;
        }

        public static Version CurrentVersion =>
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

        public static async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            var current = CurrentVersion;

            // Prefer /releases/latest, but if it has no usable .exe yet (CI race: release
            // published before asset finished uploading), retry and then scan recent releases.
            JsonElement? release = null;
            string? downloadUrl = null;
            string? lastAssetSummary = null;

            for (int attempt = 1; attempt <= AssetRetryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var latestDoc = await GetJsonAsync(
                    $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest",
                    cancellationToken);

                var latestRoot = latestDoc.RootElement.Clone();
                downloadUrl = FindExeDownloadUrl(latestRoot, out lastAssetSummary);
                if (downloadUrl is not null)
                {
                    release = latestRoot;
                    break;
                }

                // Latest exists but no .exe yet — wait for CI asset upload.
                if (attempt < AssetRetryCount)
                    await Task.Delay(AssetRetryDelayMs, cancellationToken);
            }

            if (downloadUrl is null)
            {
                // Fall back: walk recent releases for the newest one that has an .exe.
                using var listDoc = await GetJsonAsync(
                    $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=10",
                    cancellationToken);

                foreach (var item in listDoc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                        continue;
                    if (item.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                        continue;

                    downloadUrl = FindExeDownloadUrl(item, out lastAssetSummary);
                    if (downloadUrl is not null)
                    {
                        release = item.Clone();
                        break;
                    }
                }
            }

            if (release is null || downloadUrl is null)
            {
                throw new InvalidOperationException(
                    "No downloadable .exe was found in the latest release." +
                    (string.IsNullOrWhiteSpace(lastAssetSummary)
                        ? " (no assets listed — the release may still be publishing; try again in a minute)"
                        : $" Assets seen: {lastAssetSummary}"));
            }

            string tagName = release.Value.GetProperty("tag_name").GetString()
                ?? throw new InvalidOperationException("Release is missing a version tag.");

            var latest = ParseVersion(tagName);

            return new UpdateCheckResult
            {
                CurrentVersion = current,
                LatestVersion = latest,
                DownloadUrl = downloadUrl,
                UpdateAvailable = CompareVersions(current, latest) < 0
            };
        }

        public static async Task<string> DownloadUpdateAsync(
            string downloadUrl,
            Version version,
            Action<long, long?> reportProgress,
            CancellationToken cancellationToken = default)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClipsManager");
            Directory.CreateDirectory(tempDir);

            string fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = $"ClipsManager-{version.Major}.{version.Minor}.{GetBuild(version)}.exe";

            string destinationPath = Path.Combine(tempDir, fileName);
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);

            using var response = await DownloadHttp.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = File.Create(destinationPath);

            var buffer = new byte[81920];
            long downloaded = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                downloaded += bytesRead;
                reportProgress(downloaded, totalBytes);
            }

            return destinationPath;
        }

        public static void ApplyUpdate(string downloadedPath)
        {
            string targetPath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Could not determine the application path.");

            int pid = Environment.ProcessId;
            string scriptPath = Path.Combine(Path.GetTempPath(), "ClipsManager", $"update_{pid}.bat");

            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);

            // Use copy+delete instead of move so cross-volume updates work.
            string script = $"""
                @echo off
                :wait
                tasklist /FI "PID eq {pid}" 2>NUL | find "{pid}" >Nul
                if %ERRORLEVEL%==0 (
                    timeout /t 1 /nobreak >nul
                    goto wait
                )
                copy /Y "{downloadedPath}" "{targetPath}" >nul
                if errorlevel 1 (
                    ping -n 3 127.0.0.1 >nul
                    copy /Y "{downloadedPath}" "{targetPath}" >nul
                )
                del /F /Q "{downloadedPath}" >nul 2>&1
                start "" "{targetPath}"
                del /F /Q "%~f0"
                """;

            File.WriteAllText(scriptPath, script);

            Process.Start(new ProcessStartInfo
            {
                FileName = scriptPath,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
        {
            using var response = await ApiHttp.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Prefer ClipsManager-*.exe / VideoTrayApp-*.exe, then any other .exe asset.
        /// </summary>
        private static string? FindExeDownloadUrl(JsonElement release, out string assetSummary)
        {
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                assetSummary = "(missing assets array)";
                return null;
            }

            string? preferred = null;
            string? anyExe = null;
            var names = new List<string>();

            foreach (var asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                names.Add(name);

                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip symbols / installers we never ship as the app binary.
                if (name.EndsWith(".pdb.exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? url = asset.TryGetProperty("browser_download_url", out var urlEl)
                    ? urlEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                bool isPreferred =
                    name.StartsWith("ClipsManager-", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("ClipsManager.", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("VideoTrayApp-", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("ClipsManager.exe", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("VideoTrayApp.exe", StringComparison.OrdinalIgnoreCase);

                if (isPreferred)
                {
                    preferred = url;
                    break;
                }

                anyExe ??= url;
            }

            assetSummary = names.Count == 0 ? "(none)" : string.Join(", ", names);
            return preferred ?? anyExe;
        }

        private static Version ParseVersion(string tagName)
        {
            string versionText = tagName.Trim();
            if (versionText.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                versionText = versionText[1..];

            int suffixIndex = versionText.IndexOfAny(['-', '+']);
            if (suffixIndex >= 0)
                versionText = versionText[..suffixIndex];

            return Version.Parse(versionText);
        }

        private static int CompareVersions(Version current, Version latest)
        {
            int major = current.Major.CompareTo(latest.Major);
            if (major != 0) return major;

            int minor = current.Minor.CompareTo(latest.Minor);
            if (minor != 0) return minor;

            return GetBuild(current).CompareTo(GetBuild(latest));
        }

        private static int GetBuild(Version version) => version.Build < 0 ? 0 : version.Build;
    }
}
