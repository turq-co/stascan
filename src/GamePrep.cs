using System.Text;

namespace StaScan;

/// <summary>What a game prep run should do.</summary>
sealed record PrepOptions(
    List<ProcessGroup> Groups,
    bool FreeMemory,
    bool RestartExplorer,
    bool DnsAndTemp,
    List<string>? BackgroundIds,
    bool GentleMemory = false);

/// <summary>Outcome of a game prep run. <see cref="PausedIds"/> lists background items this run paused.</summary>
sealed record PrepResult(string Full, string Brief, bool Warn, List<string> PausedIds);

/// <summary>The game prep pipeline, shared by the Game prep section, game mode and hotkeys.</summary>
static class GamePrep
{
    const ulong NonpagedWarnBytes = 1536UL * 1024 * 1024; // 1.5 GB: above this a driver leak is likely

    public static PrepResult Run(PrepOptions o, Action<string> status)
    {
        var full = new StringBuilder();
        var brief = new StringBuilder();
        bool warn = false;
        var pausedIds = new List<string>();

        if (o.Groups.Count > 0)
        {
            int closed = ProcessKiller.Close(o.Groups, status);
            full.AppendLine(L.F("Processes closed: {0}.", closed));
            brief.Append(L.F("Processes closed: {0}.", closed) + " ");
        }

        if (o.BackgroundIds is { Count: > 0 } ids)
        {
            status(L.T("Pausing Windows background services…"));
            var before = BackgroundServices.LoadState().Select(e => e.Id).ToHashSet();
            var (ok, errors) = BackgroundServices.Apply(ids, PauseMode.Temporary);
            pausedIds = BackgroundServices.LoadState().Select(e => e.Id).Where(id => !before.Contains(id)).ToList();
            full.AppendLine(L.F("Background services paused: {0}.", ok) + (errors.Count > 0 ? " " + L.F("Failed: {0}", string.Join("; ", errors)) : "") + " " + L.T("To restore them, use the Background services section or the tray menu."));
            brief.Append(L.F("Background services paused: {0}.", ok) + " ");
            if (errors.Count > 0) warn = true;
        }

        if (o.DnsAndTemp)
        {
            status(L.T("Flushing DNS and cleaning TEMP…"));
            bool dns = SystemTweaks.FlushDns();
            var (files, bytes) = SystemTweaks.CleanTemp();
            full.AppendLine(L.F(dns ? "DNS cache flushed, files deleted from TEMP: {0} ({1})." : "DNS cache not flushed, files deleted from TEMP: {0} ({1}).", files, Native.Mb(bytes)));
        }

        if (o.RestartExplorer)
        {
            status(L.T("Restarting File Explorer…"));
            bool ok = SystemTweaks.RestartExplorer();
            full.AppendLine(ok ? L.T("File Explorer restarted.") : L.T("Could not restart File Explorer (see the log)."));
            if (!ok) warn = true;
        }

        if (o.FreeMemory)
        {
            status(L.T("Freeing up RAM…"));
            var r = MemoryCleaner.Clean(o.GentleMemory);
            full.AppendLine(L.F("Free RAM: {0} → {1}, standby: {2} → {3}.", MemoryCleaner.Fmt(r.Before?.FreeBytes), MemoryCleaner.Fmt(r.After?.FreeBytes), MemoryCleaner.Fmt(r.Before?.StandbyBytes), MemoryCleaner.Fmt(r.After?.StandbyBytes)));
            brief.Append(L.F("Free RAM: {0} → {1}.", MemoryCleaner.Fmt(r.Before?.FreeBytes), MemoryCleaner.Fmt(r.After?.FreeBytes)) + " ");
            if (r.Errors.Count > 0)
            {
                full.AppendLine(L.F("Memory cleanup errors: {0}", string.Join("; ", r.Errors)));
                warn = true;
            }

            full.Append(L.F("Nonpaged pool: {0}.", Native.Mb(r.NonpagedPool)));
            if (r.NonpagedPool > NonpagedWarnBytes)
            {
                var leak = " " + L.T("That is high: a driver is probably leaking memory, and only a reboot will reclaim it.");
                full.Append(leak);
                brief.Append(leak);
                warn = true;
            }
        }

        if (brief.Length == 0) brief.Append(L.T("Done."));
        FpsMeter.LastPrepAt = DateTime.Now;
        return new PrepResult(full.ToString().Trim(), brief.ToString().Trim(), warn, pausedIds);
    }
}
