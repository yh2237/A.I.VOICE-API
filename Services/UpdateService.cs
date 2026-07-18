using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace AIVoiceApi.Services;

public sealed class UpdateService
{
    public static readonly string CurrentVersion =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "0.0.0";

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string _repo;
    private readonly ILogger<UpdateService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private volatile int _updateBusy;

    public UpdateService(IConfiguration config, ILogger<UpdateService> logger, IHostApplicationLifetime lifetime)
    {
        _repo = Environment.GetEnvironmentVariable("UPDATE_GITHUB_REPO")
            ?? config["Update:GitHubRepo"]
            ?? "yh2237/A.I.VOICE-API";
        _logger = logger;
        _lifetime = lifetime;
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("A.I.VOICE-API-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.Timeout = TimeSpan.FromMinutes(5);
        return http;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{_repo}/releases/latest", ct);
        using var doc = JsonDocument.Parse(json);

        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        var latest = tag.TrimStart('v', 'V');

        string? assetName = null, assetUrl = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("A.I.VOICE-API", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    assetName = name;
                    assetUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }

        return new UpdateCheckResult
        {
            CurrentVersion = CurrentVersion,
            LatestVersion = latest,
            UpdateAvailable = assetUrl != null && IsNewer(latest, CurrentVersion),
            AssetName = assetName,
            AssetUrl = assetUrl,
        };
    }

    public async Task<UpdateStartResult> StartUpdateAsync(bool force, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _updateBusy, 1, 0) != 0)
            throw new InvalidOperationException("Update already in progress");

        try
        {
            var check = await CheckAsync(ct);

            if (!check.UpdateAvailable && !force)
            {
                _updateBusy = 0;
                return new UpdateStartResult
                {
                    Updating = false,
                    CurrentVersion = CurrentVersion,
                    TargetVersion = check.LatestVersion,
                    Message = "Already up to date",
                };
            }

            if (check.AssetUrl == null)
                throw new InvalidOperationException("No release asset (A.I.VOICE-API_*.zip) found");

            var workDir = Path.Combine(Path.GetTempPath(), "aivoice-api-update");
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            Directory.CreateDirectory(workDir);

            var zipPath = Path.Combine(workDir, check.AssetName ?? "update.zip");
            _logger.LogInformation("Downloading update: {Url}", check.AssetUrl);
            await using (var src = await Http.GetStreamAsync(check.AssetUrl, ct))
            await using (var dst = File.Create(zipPath))
            {
                await src.CopyToAsync(dst, ct);
            }

            var stagedDir = Path.Combine(workDir, "staged");
            ZipFile.ExtractToDirectory(zipPath, stagedDir);

            var exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine current executable path");
            var exeName = Path.GetFileName(exePath);
            var newExe = Directory.GetFiles(stagedDir, exeName, SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException($"{exeName} not found in release zip");
            var srcRoot = Path.GetDirectoryName(newExe)!;

            var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var updaterSrc = Path.Combine(installDir, "update.bat");
            if (!File.Exists(updaterSrc))
                throw new InvalidOperationException($"update.bat not found in {installDir}");
            var updaterBat = Path.Combine(workDir, "run_update.bat");
            File.Copy(updaterSrc, updaterBat, true);

            await File.WriteAllTextAsync(Path.Combine(installDir, "update.lock"),
                $"updating to {check.LatestVersion} at {DateTimeOffset.Now:O}", ct);

            _logger.LogInformation("Starting updater: {Current} -> {Target}", CurrentVersion, check.LatestVersion);
            Process.Start(new ProcessStartInfo
            {
                FileName = updaterBat,
                Arguments = $"{Environment.ProcessId} \"{srcRoot}\" \"{installDir}\" \"{exePath}\"",
                WorkingDirectory = workDir,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            })?.Dispose();

            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                _logger.LogInformation("Shutting down for update...");
                _lifetime.StopApplication();
            });

            return new UpdateStartResult
            {
                Updating = true,
                CurrentVersion = CurrentVersion,
                TargetVersion = check.LatestVersion,
                Message = "Update started. Server will restart shortly.",
            };
        }
        catch
        {
            _updateBusy = 0;
            try { File.Delete(Path.Combine(AppContext.BaseDirectory, "update.lock")); } catch { }
            throw;
        }
    }

    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(Normalize(latest), out var l) && Version.TryParse(Normalize(current), out var c))
            return l > c;
        return latest != current && latest.Length > 0;

        static string Normalize(string v)
        {
            var i = v.IndexOfAny(['-', '+']);
            return i >= 0 ? v[..i] : v;
        }
    }
}

public sealed class UpdateCheckResult
{
    public required string CurrentVersion { get; init; }
    public required string LatestVersion { get; init; }
    public required bool UpdateAvailable { get; init; }
    public string? AssetName { get; init; }
    public string? AssetUrl { get; init; }
}

public sealed class UpdateStartResult
{
    public required bool Updating { get; init; }
    public required string CurrentVersion { get; init; }
    public string? TargetVersion { get; init; }
    public string? Message { get; init; }
}
