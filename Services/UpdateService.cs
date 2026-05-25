using System.Net.Http;
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
    public string?           InstallerUrl  { get; init; }
    public string?           ReleaseNotes  { get; init; }
    public string?           ErrorMessage  { get; init; }
}

public static class UpdateService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

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

            var installer = release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            return new UpdateCheckInfo
            {
                Result        = IsNewer(latestTag, current)
                                    ? UpdateCheckResult.UpdateAvailable
                                    : UpdateCheckResult.UpToDate,
                LatestVersion = latestTag,
                ReleaseUrl    = release.HtmlUrl,
                InstallerUrl  = installer?.BrowserDownloadUrl,
                ReleaseNotes  = release.Body
            };
        }
        catch (Exception ex)
        {
            return Err(ex.Message);
        }
    }

    public static async Task<string> DownloadInstallerAsync(
        string url, string currentVersion, Action<double>? progress = null)
    {
        var dir  = Path.Combine(Path.GetTempPath(), "BMSMonitorUpdate");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "BMSMonitorSetup.exe");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("BMSMonitor/" + currentVersion);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(path);

        var buf  = new byte[81920];
        long done = 0;
        int  read;
        while ((read = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read));
            done += read;
            if (total > 0) progress?.Invoke((double)done / total);
        }

        return path;
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
