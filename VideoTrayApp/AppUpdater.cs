using System.Diagnostics;
using System.Net.Http;
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

        private static readonly HttpClient Http = new()
        {
            DefaultRequestHeaders =
            {
                { "User-Agent", "ClipsManager-Updater" },
                { "Accept", "application/vnd.github+json" }
            }
        };

        public static Version CurrentVersion =>
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

        public static async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            var current = CurrentVersion;
            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

            using var response = await Http.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            string tagName = root.GetProperty("tag_name").GetString()
                ?? throw new InvalidOperationException("Release is missing a version tag.");

            var latest = ParseVersion(tagName);
            string? downloadUrl = null;

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string? name = asset.GetProperty("name").GetString();
                if (name is not null
                    && (name.StartsWith("ClipsManager-", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("VideoTrayApp-", StringComparison.OrdinalIgnoreCase))
                    && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (downloadUrl is null)
                throw new InvalidOperationException("No downloadable .exe was found in the latest release.");

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
            string tempDir = Path.Combine(Path.GetTempPath(), "ClipsManager", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            string fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = $"ClipsManager-{version.Major}.{version.Minor}.{GetBuild(version)}.exe";

            string destinationPath = Path.Combine(tempDir, fileName);

            using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long downloaded = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                downloaded += bytesRead;
                reportProgress(downloaded, totalBytes);
            }

            if (downloaded == 0 || totalBytes.HasValue && downloaded != totalBytes.Value)
                throw new IOException("Update download is incomplete. Retry before installing.");
            return destinationPath;
        }

        public static void ApplyUpdate(string downloadedPath)
        {
            string targetPath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Could not determine the application path.");

            int pid = Environment.ProcessId;
            using (var package = File.OpenRead(downloadedPath))
                if (package.Length < 2 || package.ReadByte() != 'M' || package.ReadByte() != 'Z')
                    throw new InvalidDataException("Downloaded package is not a Windows executable.");
            string scriptPath = Path.Combine(Path.GetTempPath(), "ClipsManager", $"update_{pid}_{Guid.NewGuid():N}.ps1");

            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);

            string script = BuildUpdateScript(pid, downloadedPath, targetPath,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipsManager", "update_error.log"));

            File.WriteAllText(scriptPath, script);

            var helper = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath }) helper.ArgumentList.Add(argument);
            Process.Start(helper);
        }

        internal static string BuildUpdateScript(int processId, string package, string application, string log)
        {
            static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
            return $$"""
                $ErrorActionPreference = 'Stop'
                $updatePackagePath = {{Literal(package)}}
                $updateApplicationPath = {{Literal(application)}}
                $updateLogPath = {{Literal(log)}}
                $updateBackupPath = $updateApplicationPath + '.' + [guid]::NewGuid().ToString('N') + '.update-backup'
                try {
                    while (Get-Process -Id {{processId}} -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 250 }
                    Move-Item -LiteralPath $updateApplicationPath -Destination $updateBackupPath
                    try { Move-Item -LiteralPath $updatePackagePath -Destination $updateApplicationPath }
                    catch { Move-Item -LiteralPath $updateBackupPath -Destination $updateApplicationPath; throw }
                    Start-Process -FilePath $updateApplicationPath
                    Remove-Item -LiteralPath $updateBackupPath
                    if (Test-Path -LiteralPath $updateLogPath) { Remove-Item -LiteralPath $updateLogPath }
                } catch {
                    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($updateLogPath)) -Force | Out-Null
                    ($_ | Out-String) + "`nRetained backup: $updateBackupPath" | Set-Content -LiteralPath $updateLogPath
                } finally { Remove-Item -LiteralPath $PSCommandPath -ErrorAction SilentlyContinue }
                """;
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
