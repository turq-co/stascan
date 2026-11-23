using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace StaScan;

/// <summary>A newer release found on GitHub.</summary>
sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? SetupUrl, string? SetupName, long SetupSize, string? Sha256);

/// <summary>
/// Checks GitHub Releases for a newer StaScan and installs it. The request goes to the public GitHub API
/// and contains nothing but a generic User-Agent; it can be turned off in Settings.
/// </summary>
static class Updater
{
    const string LatestUrl = "https://api.github.com/repos/turq-co/stascan/releases/latest";
    public const string ReleasesPage = "https://github.com/turq-co/stascan/releases";

    public static Version Current { get; } = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);

    static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StaScan-Updater", Current.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>Returns the latest release if it is newer than this build, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var http = Client();
        using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestUrl));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        if (latest <= new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build))) return null;

        string? url = null, name = null, sha = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var n = asset.GetProperty("name").GetString() ?? "";
            if (!n.StartsWith("StaScan-Setup-", StringComparison.OrdinalIgnoreCase) || !n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            url = asset.GetProperty("browser_download_url").GetString();
            name = n;
            size = asset.GetProperty("size").GetInt64();
            if (asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:"))
                sha = digest[7..];
        }
        return new UpdateInfo(latest, tag, root.GetProperty("html_url").GetString() ?? ReleasesPage, url, name, size, sha);
    }

    /// <summary>Downloads the installer to %TEMP%, verifies its size and SHA-256, and returns the file path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<int> progress)
    {
        if (update.SetupUrl == null || update.SetupName == null) throw new InvalidOperationException(L.T("the release has no installer attached"));
        var path = Path.Combine(Path.GetTempPath(), update.SetupName);
        using var http = Client();
        http.Timeout = TimeSpan.FromMinutes(10);
        using (var response = await http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(path);
            var buffer = new byte[1 << 16];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                total += read;
                if (update.SetupSize > 0) progress.Report((int)(total * 100 / update.SetupSize));
            }
        }

        if (update.SetupSize > 0 && new FileInfo(path).Length != update.SetupSize)
            throw new InvalidOperationException(L.T("the download is incomplete"));
        if (update.Sha256 != null)
        {
            await using var fs = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
            if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L.T("the installer checksum does not match the release"));
        }
        Log.Write($"Update {update.Tag} downloaded to {path}");
        return path;
    }

    /// <summary>
    /// Runs the installer silently. It closes StaScan, installs over the current version, keeps the
    /// previously chosen options (autostart, shortcuts) and starts the new version.
    /// </summary>
    public static void Install(string setupPath)
    {
        Log.Write($"Running installer {setupPath}");
        Process.Start(new ProcessStartInfo(setupPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true });
    }
}
