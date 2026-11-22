using System.Diagnostics;

namespace StaScan;

static class ProcessKiller
{
    /// <summary>
    /// First asks windows to close (like clicking ×), then terminates whatever is still running after 5 seconds.
    /// Returns the number of processes that ended.
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
                    // the PID may have been reused by another process while the window was open
                    if (string.Equals(Native.GetProcessPath(pid), g.Path, StringComparison.OrdinalIgnoreCase))
                        procs.Add(p);
                    else
                        p.Dispose();
                }
                catch
                {
                    // already exited
                }
            }
        }

        status($"Closing windows ({procs.Count} processes)…");
        var asked = new List<Process>();
        foreach (var p in procs)
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow()) asked.Add(p);
            }
            catch { }
        }

        // only wait for processes that were asked to close; windowless ones will not react anyway
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && asked.Any(p => !HasExited(p)))
            Thread.Sleep(250);

        status("Terminating the remaining processes…");
        int closed = 0;
        foreach (var p in procs)
        {
            try
            {
                if (!HasExited(p))
                {
                    // not the whole tree: new child processes are listed anyway,
                    // and whitelisted processes must not be affected
                    p.Kill(entireProcessTree: false);
                    p.WaitForExit(2000);
                }
                closed++;
            }
            catch (Exception ex)
            {
                Log.Write($"Could not terminate {SafeName(p)} (PID {SafeId(p)}): {ex.Message}");
            }
            finally
            {
                p.Dispose();
            }
        }

        Log.Write($"Processes ended: {closed} of {procs.Count}");
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
