namespace StaScan;

sealed record MemoryReport(Native.MemoryLists? Before, Native.MemoryLists? After, ulong NonpagedPool, List<string> Errors);

static class MemoryCleaner
{
    /// <summary>
    /// Full cleanup (before a game starts): empties working sets, the modified list, the file cache and the
    /// standby list, like RAMMap → Empty.
    ///
    /// Gentle cleanup (while a game is running): only purges the low-priority standby list. It never touches the
    /// working set of the running game or the file cache it is reading assets from, because forcing those to be
    /// reloaded causes hitches (most noticeable on fast camera turns).
    /// </summary>
    public static MemoryReport Clean(bool gentle = false)
    {
        var errors = new List<string>();
        if (!Native.EnablePrivilege("SeProfileSingleProcessPrivilege"))
            errors.Add(L.T("SeProfileSingleProcessPrivilege is not available (is the app running without admin rights?)"));
        if (!gentle && !Native.EnablePrivilege("SeIncreaseQuotaPrivilege"))
            errors.Add("SeIncreaseQuotaPrivilege is not available");

        var before = Native.QueryMemoryLists();

        if (gentle)
        {
            Command(Native.MemoryPurgeLowPriorityStandbyList, "low-priority standby list", errors);
        }
        else
        {
            Command(Native.MemoryEmptyWorkingSets, "working sets", errors);

            if (!Native.SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), 0))
                errors.Add($"file cache flush: error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");

            Command(Native.MemoryFlushModifiedList, "modified list", errors);
            Command(Native.MemoryPurgeStandbyList, "standby list", errors);
        }

        var after = Native.QueryMemoryLists();
        var report = new MemoryReport(before, after, Native.GetNonpagedPoolBytes(), errors);

        Log.Write($"Memory cleanup ({(gentle ? "gentle" : "full")}): free {Fmt(before?.FreeBytes)} → {Fmt(after?.FreeBytes)}, " +
                  $"standby {Fmt(before?.StandbyBytes)} → {Fmt(after?.StandbyBytes)}, " +
                  $"nonpaged pool {Native.Mb(report.NonpagedPool)}" +
                  (errors.Count > 0 ? "; errors: " + string.Join("; ", errors) : ""));
        return report;
    }

    static void Command(int command, string what, List<string> errors)
    {
        int cmd = command;
        int status = Native.NtSetSystemInformation(Native.SystemMemoryListInformation, ref cmd, sizeof(int));
        if (status != 0) errors.Add($"{what}: NTSTATUS 0x{status:X8}");
    }

    public static string Fmt(ulong? bytes) => bytes is { } b ? Native.Gb(b) : "?";
}
