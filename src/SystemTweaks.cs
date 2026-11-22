using System.Diagnostics;

namespace StaScan;

static class SystemTweaks
{
    /// <summary>
    /// Перезапуск проводника. Explorer завершаем, а поднимает его сам Winlogon (AutoRestartShell),
    /// поэтому он стартует с обычными правами, а не с правами администратора, как StaScan.
    /// Если за 6 секунд он не появился, запускаем через задачу планировщика без повышения прав.
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
                Log.Write($"explorer не завершён: {ex.Message}");
            }
            finally
            {
                p.Dispose();
            }
        }

        if (WaitForExplorer(session, oldIds, 6000))
        {
            Log.Write("Explorer перезапущен (Winlogon)");
            return true;
        }

        Log.Write("Explorer сам не поднялся, запускаю через задачу StaScanExplorer");
        if (Autostart.RunExplorerTask() && WaitForExplorer(session, oldIds, 6000))
        {
            Log.Write("Explorer перезапущен (задача планировщика)");
            return true;
        }

        Log.Write("Не удалось перезапустить explorer");
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
                Thread.Sleep(1500); // даём панели задач прорисоваться
                return true;
            }
            Thread.Sleep(300);
        }
        return false;
    }

    public static bool FlushDns()
    {
        bool ok = Native.DnsFlushResolverCache();
        Log.Write(ok ? "DNS-кэш сброшен" : "DNS-кэш не сброшен");
        return ok;
    }

    /// <summary>Удаляет из %TEMP% всё старше суток. Занятые файлы пропускает.</summary>
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
                    // файл занят
                }
            }

            // пустые папки, самые глубокие первыми
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
            Log.Write($"Очистка TEMP прервана: {ex.Message}");
        }

        Log.Write($"TEMP: удалено файлов {files}, {Native.Mb(bytes)}");
        return (files, bytes);
    }
}
