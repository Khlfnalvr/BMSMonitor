using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BMSMonitor.Services;

public record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("body")]     string? Body,
    [property: JsonPropertyName("assets")]   GitHubAsset[] Assets
);

public record GitHubAsset(
    [property: JsonPropertyName("name")]                 string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
    [property: JsonPropertyName("size")]                 long   Size
);

public enum UpdateCheckResult { UpToDate, UpdateAvailable, Error }

public sealed class UpdateCheckInfo
{
    public UpdateCheckResult Result        { get; init; }
    public string?           LatestVersion { get; init; }
    public string?           ReleaseUrl    { get; init; }
    public string?           UpdateZipUrl  { get; init; }
    public string?           ReleaseNotes  { get; init; }
    public string?           ErrorMessage  { get; init; }
}

public static class UpdateService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private const string ApiUrl =
        "https://api.github.com/repos/Khlfnalvr/BMSMonitor/releases/latest";

    public static async Task<UpdateCheckInfo> CheckAsync(string currentVersion)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            req.Headers.UserAgent.ParseAdd("BMSMonitor/" + currentVersion);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            var json    = await resp.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize(json, AppJsonContext.Default.GitHubRelease);
            if (release is null)
                return Err("Invalid API response");

            var latestTag = release.TagName.TrimStart('v');
            var current   = currentVersion.TrimStart('v');

            var zipAsset = release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            return new UpdateCheckInfo
            {
                Result        = IsNewer(latestTag, current)
                                    ? UpdateCheckResult.UpdateAvailable
                                    : UpdateCheckResult.UpToDate,
                LatestVersion = latestTag,
                ReleaseUrl    = release.HtmlUrl,
                UpdateZipUrl  = zipAsset?.BrowserDownloadUrl,
                ReleaseNotes  = release.Body
            };
        }
        catch (Exception ex)
        {
            return Err(ex.Message);
        }
    }

    // Downloads the update ZIP and extracts it to a staging folder.
    // Returns the staging directory path where the new app files live.
    public static async Task<string> DownloadAndExtractAsync(
        string url, string currentVersion, Action<double>? progress = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "BMSMonitorUpdate");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "update.zip");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("BMSMonitor/" + currentVersion);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using (var dst = File.Create(zipPath))
        {
            var buf  = new byte[81920];
            long done = 0;
            int  read;
            while ((read = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read));
                done += read;
                if (total > 0) progress?.Invoke((double)done / total * 0.85);
            }
        }

        // Extract to staging subfolder
        var stagePath = Path.Combine(tempDir, "stage");
        if (Directory.Exists(stagePath)) Directory.Delete(stagePath, true);
        Directory.CreateDirectory(stagePath);
        ZipFile.ExtractToDirectory(zipPath, stagePath);
        File.Delete(zipPath);

        progress?.Invoke(1.0);
        return stagePath;
    }

    // Writes a PowerShell script that, once launched, waits for BMSMonitor
    // to exit, copies all staged files over the installed app, then restarts it.
    // Returns the path to the written script.
    public static string WriteApplyScript(string stagingPath, string appDir)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "BMSMonitorUpdate", "apply.ps1");

        var sb = new StringBuilder();
        sb.AppendLine("param($Staging, $AppDir)");
        sb.AppendLine("# Wait for BMSMonitor to fully exit");
        sb.AppendLine("while (Get-Process -Name BMSMonitor -ErrorAction SilentlyContinue) {");
        sb.AppendLine("    Start-Sleep -Milliseconds 500");
        sb.AppendLine("}");
        sb.AppendLine("# Copy update files over the installed app");
        sb.AppendLine("robocopy $Staging $AppDir /E /IS /IT /NJH /NJS /NFL /NDL /COPY:DAT | Out-Null");
        sb.AppendLine("# Restart the app");
        sb.AppendLine("Start-Process (Join-Path $AppDir 'BMSMonitor.exe')");
        sb.AppendLine("# Cleanup");
        sb.AppendLine("Remove-Item $Staging -Recurse -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Remove-Item $PSCommandPath -Force -ErrorAction SilentlyContinue");

        File.WriteAllText(scriptPath, sb.ToString(), Encoding.UTF8);
        return scriptPath;
    }

    private static UpdateCheckInfo Err(string msg) =>
        new() { Result = UpdateCheckResult.Error, ErrorMessage = msg };

    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(latest, out var l) && Version.TryParse(current, out var c))
            return l > c;
        return string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) > 0;
    }
}
