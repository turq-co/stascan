using System.Diagnostics;

namespace StaScan;

static class SystemTweaks
{
    /// <summary>
    /// Restarts File Explorer. We terminate explorer and let Winlogon bring it back (AutoRestartShell),
    /// so it starts with normal rights rather than the administrator rights StaScan has.
    /// If it does not come back within 6 seconds, it is started through a non-elevated scheduled task.
    /// </summary>
    public static bool RestartExplorer()
    {
        int session = Process.GetCurrentProcess().SessionId;
        var old = Process.GetProcessesByName("explorer").Where(p => p.SessionId == session).ToList();
        var oldIds = old.Select(p => p.Id).ToHashSet();

        foreach (var p in old)
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                Log.Write($"Could not terminate explorer: {ex.Message}");
            }
            finally
            {
                p.Dispose();
            }
        }

        if (WaitForExplorer(session, oldIds, 6000))
        {
            Log.Write("Explorer restarted (Winlogon)");
            return true;
        }

        Log.Write("Explorer did not come back by itself, starting it through the StaScanExplorer task");
        if (Autostart.RunExplorerTask() && WaitForExplorer(session, oldIds, 6000))
        {
            Log.Write("Explorer restarted (scheduled task)");
            return true;
        }

        Log.Write("Could not restart explorer");
        return false;
    }

    static bool WaitForExplorer(int session, HashSet<int> oldIds, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var found = Process.GetProcessesByName("explorer");
            bool ok = found.Any(p => p.SessionId == session && !oldIds.Contains(p.Id));
            foreach (var p in found) p.Dispose();
            if (ok)
            {
                Thread.Sleep(1500); // give the taskbar time to draw
                return true;
            }
            Thread.Sleep(300);
        }
        return false;
    }

    public static bool FlushDns()
    {
        bool ok = Native.DnsFlushResolverCache();
        Log.Write(ok ? "DNS cache flushed" : "DNS cache was not flushed");
        return ok;
    }

    /// <summary>Deletes everything older than 24 hours from %TEMP%. Files in use are skipped.</summary>
    public static (int files, ulong bytes) CleanTemp()
    {
        var root = new DirectoryInfo(Path.GetTempPath());
        var cutoff = DateTime.Now.AddDays(-1);
        int files = 0;
        ulong bytes = 0;

        try
        {
            foreach (var f in root.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                try
                {
                    if (f.LastWriteTime > cutoff) continue;
                    long len = f.Length;
                    f.Attributes = FileAttributes.Normal;
                    f.Delete();
                    files++;
                    bytes += (ulong)len;
                }
                catch
                {
                    // file in use
                }
            }

            // empty folders, deepest first
            var dirs = root.EnumerateDirectories("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .OrderByDescending(d => d.FullName.Length);
            foreach (var d in dirs)
            {
                try
                {
                    if (d.LastWriteTime <= cutoff && !d.EnumerateFileSystemInfos().Any()) d.Delete();
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"TEMP cleanup interrupted: {ex.Message}");
        }

        Log.Write($"TEMP: deleted {files} files, {Native.Mb(bytes)}");
        return (files, bytes);
    }
}
