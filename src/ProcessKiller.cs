using System.Diagnostics;

namespace StaScan;

static class ProcessKiller
{
    /// <summary>
    /// Сначала просит окна закрыться (как крестик), через 5 секунд завершает то, что осталось.
    /// Возвращает число завершённых процессов.
    /// </summary>
    public static int Close(IEnumerable<ProcessGroup> groups, Action<string> status)
    {
        var procs = new List<Process>();
        foreach (var g in groups)
        {
            foreach (var pid in g.Pids)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    // PID мог достаться другому процессу, пока окно было открыто
                    if (string.Equals(Native.GetProcessPath(pid), g.Path, StringComparison.OrdinalIgnoreCase))
                        procs.Add(p);
                    else
                        p.Dispose();
                }
                catch
                {
                    // уже завершился
                }
            }
        }

        status($"Закрываю окна ({procs.Count} процессов)…");
        var asked = new List<Process>();
        foreach (var p in procs)
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow()) asked.Add(p);
            }
            catch { }
        }

        // ждём только тех, кого попросили закрыться; безоконные процессы ждать бессмысленно
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && asked.Any(p => !HasExited(p)))
            Thread.Sleep(250);

        status("Завершаю оставшиеся процессы…");
        int closed = 0;
        foreach (var p in procs)
        {
            try
            {
                if (!HasExited(p))
                {
                    // без дерева: дочерние процессы, если они новые, и так есть в списке,
                    // а процессы из белого списка не должны пострадать
                    p.Kill(entireProcessTree: false);
                    p.WaitForExit(2000);
                }
                closed++;
            }
            catch (Exception ex)
            {
                Log.Write($"Не удалось завершить {SafeName(p)} (PID {SafeId(p)}): {ex.Message}");
            }
            finally
            {
                p.Dispose();
            }
        }

        Log.Write($"Завершено процессов: {closed} из {procs.Count}");
        return closed;
    }

    static bool HasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    static string SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return "?"; }
    }

    static string SafeId(Process p)
    {
        try { return p.Id.ToString(); } catch { return "?"; }
    }
}
