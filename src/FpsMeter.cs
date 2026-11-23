using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace StaScan;

/// <summary>One FPS measurement.</summary>
sealed record FpsResult(DateTime At, string Process, double AvgFps, double Low1Fps, int Frames, double Seconds, bool AfterPrep);

/// <summary>
/// Measures the frame rate of a running game with Intel PresentMon (MIT licence, https://github.com/GameTechDev/PresentMon).
/// PresentMon is downloaded once on request, from a pinned release, and verified by SHA-256 before use.
/// </summary>
static class FpsMeter
{
    const string Version = "2.6.0";
    const string FileName = "PresentMon-2.6.0-x64.exe";
    const string Url = "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe";
    const string Sha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
    public const int DefaultSeconds = 20;

    static string ToolPath => Path.Combine(AppPaths.Dir, "tools", FileName);
    static string HistoryPath => AppPaths.File("fps-history.json");
    static readonly object Sync = new();

    /// <summary>When game prep or a quick boost last ran; used to mark measurements taken "after prep".</summary>
    public static DateTime LastPrepAt { get; set; }

    public static bool IsInstalled => File.Exists(ToolPath) && HashOk(ToolPath);

    static bool HashOk(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs)).Equals(Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Downloads PresentMon (about 1 MB) and verifies it. Throws if the checksum does not match.</summary>
    public static async Task InstallAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ToolPath)!);
        var tmp = ToolPath + ".download";
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"StaScan/{Updater.Current.ToString(3)}");
            await using var source = await http.GetStreamAsync(Url);
            await using var target = File.Create(tmp);
            await source.CopyToAsync(target);
        }
        if (!HashOk(tmp))
        {
            File.Delete(tmp);
            throw new InvalidOperationException(L.T("the PresentMon download does not match the expected checksum"));
        }
        File.Move(tmp, ToolPath, overwrite: true);
        Log.Write($"PresentMon {Version} installed to {ToolPath}");
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    /// <summary>Executable name of the program in the foreground window (the game you are playing).</summary>
    public static string? ForegroundProcess()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out int pid);
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Records frames of <paramref name="process"/> for the given time and computes average FPS and 1% low.</summary>
    public static async Task<FpsResult> MeasureAsync(string process, int seconds = DefaultSeconds)
    {
        if (!IsInstalled) throw new InvalidOperationException(L.T("PresentMon is not installed yet. Download it in the Game mode section."));
        var csv = Path.Combine(Path.GetTempPath(), $"StaScan-fps-{Guid.NewGuid():N}.csv");
        var args = $"--process_name \"{process}\" --timed {seconds} --terminate_after_timed --output_file \"{csv}\" " +
                   "--no_console_stats --stop_existing_session --session_name StaScanFps";
        Log.Write($"FPS measurement: {process} for {seconds} s");
        try
        {
            using (var p = Process.Start(new ProcessStartInfo(ToolPath, args) { CreateNoWindow = true, UseShellExecute = false })!)
            {
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(seconds + 30));
            }
            if (!File.Exists(csv)) throw new InvalidOperationException(L.F("No frames from {0}. Is it running and drawing frames?", process));
            var result = Parse(csv, process);
            Save(result);
            Log.Write($"FPS: {process} avg {result.AvgFps:0.0}, 1% low {result.Low1Fps:0.0}, {result.Frames} frames");
            return result;
        }
        finally
        {
            try { File.Delete(csv); } catch { }
        }
    }

    /// <summary>Uses the swap chain with the most frames (games sometimes create extra ones for launchers or overlays).</summary>
    static FpsResult Parse(string csv, string process)
    {
        var lines = File.ReadAllLines(csv);
        if (lines.Length < 2) throw new InvalidOperationException(L.F("No frames from {0}. Is it running and drawing frames?", process));
        var header = lines[0].Split(',');
        int swap = Array.IndexOf(header, "SwapChainAddress");
        int frame = Array.IndexOf(header, "MsBetweenPresents");
        if (frame < 0) throw new InvalidOperationException("PresentMon output has no MsBetweenPresents column");

        var bySwapChain = new Dictionary<string, List<double>>();
        foreach (var line in lines.Skip(1))
        {
            var cols = line.Split(',');
            if (cols.Length <= frame) continue;
            if (!double.TryParse(cols[frame], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms <= 0) continue;
            var key = swap >= 0 && cols.Length > swap ? cols[swap] : "";
            if (!bySwapChain.TryGetValue(key, out var list)) bySwapChain[key] = list = new List<double>();
            list.Add(ms);
        }
        var times = bySwapChain.Values.OrderByDescending(l => l.Count).FirstOrDefault();
        if (times == null || times.Count < 10)
            throw new InvalidOperationException(L.F("No frames from {0}. Is it running and drawing frames?", process));

        double total = times.Sum();
        double avgFps = times.Count / (total / 1000.0);
        var sorted = times.OrderBy(t => t).ToList();
        double p99 = sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(sorted.Count * 0.99) - 1)];
        bool afterPrep = LastPrepAt > DateTime.MinValue &&
                         Load().LastOrDefault(r => r.Process.Equals(process, StringComparison.OrdinalIgnoreCase)) is { } prev &&
                         LastPrepAt > prev.At;
        return new FpsResult(DateTime.Now, process, avgFps, 1000.0 / p99, times.Count, total / 1000.0, afterPrep);
    }

    public static List<FpsResult> Load()
    {
        lock (Sync)
        {
            try
            {
                if (File.Exists(HistoryPath))
                    return JsonSerializer.Deserialize<List<FpsResult>>(File.ReadAllText(HistoryPath)) ?? new();
            }
            catch (Exception ex)
            {
                Log.Write($"Could not read fps-history.json: {ex.Message}");
            }
            return new();
        }
    }

    static void Save(FpsResult result)
    {
        lock (Sync)
        {
            var all = Load();
            all.Add(result);
            if (all.Count > 50) all.RemoveRange(0, all.Count - 50);
            try { File.WriteAllText(HistoryPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { Log.Write($"Could not save fps-history.json: {ex.Message}"); }
        }
    }

    /// <summary>Short text for a notification, compared with the previous measurement of the same program.</summary>
    public static string Describe(FpsResult r)
    {
        var text = L.F("{0}: {1} FPS average, 1% low {2}.", r.Process, r.AvgFps.ToString("0", CultureInfo.InvariantCulture), r.Low1Fps.ToString("0", CultureInfo.InvariantCulture));
        var prev = Load().Where(x => x.Process.Equals(r.Process, StringComparison.OrdinalIgnoreCase) && x.At < r.At).LastOrDefault();
        if (prev != null && prev.AvgFps > 0)
            text += " " + L.F("Change since the previous measurement: {0}.", Change(prev.AvgFps, r.AvgFps));
        return text;
    }

    public static string Change(double before, double after)
    {
        double pct = (after - before) / before * 100;
        return (pct >= 0 ? "+" : "") + pct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }
}
