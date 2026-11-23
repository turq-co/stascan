using System.Text;

namespace StaScan;

/// <summary>What a game prep run should do.</summary>
sealed record PrepOptions(
    List<ProcessGroup> Groups,
    bool FreeMemory,
    bool RestartExplorer,
    bool DnsAndTemp,
    List<string>? BackgroundIds);

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
            full.AppendLine($"Processes closed: {closed}.");
            brief.Append($"Processes closed: {closed}. ");
        }

        if (o.BackgroundIds is { Count: > 0 } ids)
        {
            status("Pausing Windows background services…");
            var before = BackgroundServices.LoadState().Select(e => e.Id).ToHashSet();
            var (ok, errors) = BackgroundServices.Apply(ids, PauseMode.Temporary);
            pausedIds = BackgroundServices.LoadState().Select(e => e.Id).Where(id => !before.Contains(id)).ToList();
            full.AppendLine($"Background services paused: {ok}" +
                            (errors.Count > 0 ? $", failed: {string.Join("; ", errors)}" : "") +
                            ". To restore them, use the Background services section or the tray menu.");
            brief.Append($"Background services paused: {ok}. ");
            if (errors.Count > 0) warn = true;
        }

        if (o.DnsAndTemp)
        {
            status("Flushing DNS and cleaning TEMP…");
            bool dns = SystemTweaks.FlushDns();
            var (files, bytes) = SystemTweaks.CleanTemp();
            full.AppendLine($"DNS cache {(dns ? "flushed" : "not flushed")}, files deleted from TEMP: {files} ({Native.Mb(bytes)}).");
        }

        if (o.RestartExplorer)
        {
            status("Restarting File Explorer…");
            bool ok = SystemTweaks.RestartExplorer();
            full.AppendLine(ok ? "File Explorer restarted." : "Could not restart File Explorer (see the log).");
            if (!ok) warn = true;
        }

        if (o.FreeMemory)
        {
            status("Freeing up RAM…");
            var r = MemoryCleaner.Clean();
            full.AppendLine($"Free RAM: {MemoryCleaner.Fmt(r.Before?.FreeBytes)} → {MemoryCleaner.Fmt(r.After?.FreeBytes)}, " +
                            $"standby: {MemoryCleaner.Fmt(r.Before?.StandbyBytes)} → {MemoryCleaner.Fmt(r.After?.StandbyBytes)}.");
            brief.Append($"Free RAM: {MemoryCleaner.Fmt(r.Before?.FreeBytes)} → {MemoryCleaner.Fmt(r.After?.FreeBytes)}. ");
            if (r.Errors.Count > 0)
            {
                full.AppendLine("Memory cleanup errors: " + string.Join("; ", r.Errors));
                warn = true;
            }

            full.Append($"Nonpaged pool: {Native.Mb(r.NonpagedPool)}.");
            if (r.NonpagedPool > NonpagedWarnBytes)
            {
                const string leak = " That is high: a driver is probably leaking memory, and only a reboot will reclaim it.";
                full.Append(leak);
                brief.Append(leak);
                warn = true;
            }
        }

        if (brief.Length == 0) brief.Append("Done.");
        return new PrepResult(full.ToString().Trim(), brief.ToString().Trim(), warn, pausedIds);
    }
}
