using System.Diagnostics;

namespace StaScan;

sealed record ProcInfo(int Pid, string ExeName, string? Path, int SessionId, long WorkingSet)
{
    /// <summary>Ключ для сравнения со снимком: путь к exe, а без пути имя процесса.</summary>
    public string Key => (Path ?? ExeName).ToLowerInvariant();
}

sealed class ProcessGroup
{
    public required string ExeName { get; init; }
    public required string Path { get; init; }
    public List<int> Pids { get; } = new();
    public long WorkingSet { get; set; }
}

static class ProcessScanner
{
    /// <summary>Системные процессы, которые нельзя закрывать ни при каких условиях.</summary>
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "memory compression", "secure system",
        "csrss.exe", "smss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe",
        "svchost.exe", "dwm.exe", "sihost.exe", "ctfmon.exe", "audiodg.exe", "fontdrvhost.exe",
        "explorer.exe", "conhost.exe", "runtimebroker.exe", "taskhostw.exe", "dllhost.exe",
        "searchhost.exe", "startmenuexperiencehost.exe", "shellexperiencehost.exe", "textinputhost.exe",
        "securityhealthsystray.exe", "securityhealthservice.exe", "msmpeng.exe", "nissrv.exe",
        "applicationframehost.exe", "lockapp.exe", "userinit.exe", "wudfhost.exe",
    };

    static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    public static List<ProcInfo> Snapshot()
    {
        var result = new List<ProcInfo>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var path = Native.GetProcessPath(p.Id);
                    var exe = path != null ? System.IO.Path.GetFileName(path) : p.ProcessName + ".exe";
                    result.Add(new ProcInfo(p.Id, exe, path, p.SessionId, p.WorkingSet64));
                }
                catch
                {
                    // процесс успел завершиться
                }
            }
        }
        return result;
    }

    /// <summary>Процессы, появившиеся после снимка, сгруппированные по exe.</summary>
    public static List<ProcessGroup> FindCandidates(Baseline baseline, Settings settings)
    {
        int mySession = Process.GetCurrentProcess().SessionId;
        int myPid = Environment.ProcessId;
        var groups = new Dictionary<string, ProcessGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Snapshot())
        {
            if (p.Pid == myPid || p.SessionId != mySession) continue;
            if (p.Path == null) continue; // без пути не понять, что это: не трогаем
            if (p.Path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (Protected.Contains(p.ExeName) || settings.IsWhitelisted(p.ExeName)) continue;
            if (baseline.Contains(p.Key)) continue;

            if (!groups.TryGetValue(p.Path, out var g))
                groups[p.Path] = g = new ProcessGroup { ExeName = p.ExeName, Path = p.Path };
            g.Pids.Add(p.Pid);
            g.WorkingSet += p.WorkingSet;
        }

        return groups.Values.OrderByDescending(g => g.WorkingSet).ToList();
    }
}
