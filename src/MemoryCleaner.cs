namespace StaScan;

sealed record MemoryReport(Native.MemoryLists? Before, Native.MemoryLists? After, ulong NonpagedPool, List<string> Errors);

static class MemoryCleaner
{
    /// <summary>Сбрасывает рабочие наборы, modified list, кэш файлов и standby list (как RAMMap → Empty).</summary>
    public static MemoryReport Clean()
    {
        var errors = new List<string>();
        if (!Native.EnablePrivilege("SeProfileSingleProcessPrivilege"))
            errors.Add("нет привилегии SeProfileSingleProcessPrivilege (приложение запущено не от администратора?)");
        if (!Native.EnablePrivilege("SeIncreaseQuotaPrivilege"))
            errors.Add("нет привилегии SeIncreaseQuotaPrivilege");

        var before = Native.QueryMemoryLists();

        Command(Native.MemoryEmptyWorkingSets, "рабочие наборы", errors);

        if (!Native.SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), 0))
            errors.Add($"сброс кэша файлов: ошибка {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");

        Command(Native.MemoryFlushModifiedList, "modified list", errors);
        Command(Native.MemoryPurgeStandbyList, "standby list", errors);

        var after = Native.QueryMemoryLists();
        var report = new MemoryReport(before, after, Native.GetNonpagedPoolBytes(), errors);

        Log.Write($"Очистка ОЗУ: свободно {Fmt(before?.FreeBytes)} → {Fmt(after?.FreeBytes)}, " +
                  $"standby {Fmt(before?.StandbyBytes)} → {Fmt(after?.StandbyBytes)}, " +
                  $"nonpaged pool {Native.Mb(report.NonpagedPool)}" +
                  (errors.Count > 0 ? "; ошибки: " + string.Join("; ", errors) : ""));
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
