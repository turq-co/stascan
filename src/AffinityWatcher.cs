using System.Diagnostics;

namespace StaScan;

/// <summary>
/// Removes CPU 0 from the affinity mask of cs2.exe. CS2 is usually launched after game prep,
/// so while the option is on StaScan checks cs2.exe every 3 seconds and re-applies the mask
/// if the game or an anti-cheat resets it.
/// </summary>
sealed class AffinityWatcher : IDisposable
{
    const string ProcessName = "cs2";

    readonly Func<bool> _enabled;
    readonly Action<string, ToolTipIcon> _notify;
    readonly System.Threading.Timer _timer;
    readonly HashSet<int> _applied = new();
    readonly HashSet<int> _failed = new();
    int _busy;

    public AffinityWatcher(Func<bool> enabled, Action<string, ToolTipIcon> notify)
    {
        _enabled = enabled;
        _notify = notify;
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        Update();
    }

    /// <summary>The timer only runs while the option is on; otherwise StaScan never wakes up for it.</summary>
    public void Update()
    {
        if (_enabled()) _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        else _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Mask of all logical processors except CPU 0.</summary>
    static IntPtr TargetMask()
    {
        int n = Math.Min(Environment.ProcessorCount, 64);
        long all = n >= 64 ? -1L : (1L << n) - 1;
        return new IntPtr(all & ~1L);
    }

    public void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (!_enabled() || Environment.ProcessorCount < 2) return;

            var procs = Process.GetProcessesByName(ProcessName);
            var alive = new HashSet<int>();
            foreach (var p in procs)
            {
                using (p)
                {
                    alive.Add(p.Id);
                    if (_failed.Contains(p.Id)) continue;
                    try
                    {
                        if ((p.ProcessorAffinity.ToInt64() & 1) == 0) continue; // CPU 0 already excluded
                        p.ProcessorAffinity = TargetMask();
                        if (_applied.Add(p.Id))
                        {
                            Log.Write($"cs2.exe (PID {p.Id}): CPU 0 excluded, mask 0x{TargetMask().ToInt64():X}");
                            _notify("CS2: CPU 0 is now excluded for the game.", ToolTipIcon.Info);
                        }
                        else
                        {
                            Log.Write($"cs2.exe (PID {p.Id}): the mask was reset, applied it again");
                        }
                    }
                    catch (Exception ex)
                    {
                        _failed.Add(p.Id);
                        Log.Write($"cs2.exe (PID {p.Id}): could not change CPU affinity: {ex.Message}");
                        _notify("Could not exclude CPU 0 for CS2 (an anti-cheat may be blocking it). See the log for details.", ToolTipIcon.Warning);
                    }
                }
            }
            _applied.IntersectWith(alive);
            _failed.IntersectWith(alive);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
