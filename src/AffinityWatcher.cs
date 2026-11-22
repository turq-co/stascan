using System.Diagnostics;

namespace StaScan;

/// <summary>
/// Убирает CPU 0 из маски привязки процесса cs2.exe. CS2 обычно запускают уже после подготовки,
/// поэтому, пока опция включена, StaScan раз в 3 секунды проверяет cs2.exe и при необходимости
/// снова выставляет маску (если игра или античит её сбросили).
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

    /// <summary>Таймер работает только при включённой опции: в остальное время StaScan не просыпается.</summary>
    public void Update()
    {
        if (_enabled()) _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        else _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Маска «все логические процессоры, кроме CPU 0».</summary>
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
                        if ((p.ProcessorAffinity.ToInt64() & 1) == 0) continue; // CPU 0 уже снят
                        p.ProcessorAffinity = TargetMask();
                        if (_applied.Add(p.Id))
                        {
                            Log.Write($"cs2.exe (PID {p.Id}): CPU 0 снят, маска 0x{TargetMask().ToInt64():X}");
                            _notify("CS2: CPU 0 отключён для игры.", ToolTipIcon.Info);
                        }
                        else
                        {
                            Log.Write($"cs2.exe (PID {p.Id}): маску сбросили, выставил заново");
                        }
                    }
                    catch (Exception ex)
                    {
                        _failed.Add(p.Id);
                        Log.Write($"cs2.exe (PID {p.Id}): не удалось изменить привязку к CPU: {ex.Message}");
                        _notify("Не удалось отключить CPU 0 для CS2 (возможно, мешает античит). Подробности в логе.", ToolTipIcon.Warning);
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
