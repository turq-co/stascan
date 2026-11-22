using System.Runtime.InteropServices;
using System.Text;

namespace StaScan;

static class Native
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    const uint TOKEN_QUERY = 0x8;
    const uint SE_PRIVILEGE_ENABLED = 0x2;
    const int ERROR_NOT_ALL_ASSIGNED = 1300;

    public const int SystemMemoryListInformation = 80;
    public const int MemoryEmptyWorkingSets = 2;
    public const int MemoryFlushModifiedList = 3;
    public const int MemoryPurgeStandbyList = 4;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetSystemFileCacheSize(IntPtr minimum, IntPtr maximum, uint flags);

    [DllImport("ntdll.dll")]
    public static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, int size);

    [DllImport("dnsapi.dll")]
    public static extern bool DnsFlushResolverCache();

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState,
        int bufferLength, IntPtr previousState, IntPtr returnLength);

    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
            SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    /// <summary>Полный путь к exe процесса или null, если доступа нет.</summary>
    public static string? GetProcessPath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return false;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    public readonly record struct MemoryLists(ulong FreeBytes, ulong StandbyBytes, ulong ModifiedBytes);

    /// <summary>Списки физической памяти (как в RAMMap). Нужна SeProfileSingleProcessPrivilege.</summary>
    public static MemoryLists? QueryMemoryLists()
    {
        // SYSTEM_MEMORY_LIST_INFORMATION: Zero, Free, Modified, ModifiedNoWrite, Bad,
        // PageCountByPriority[8], RepurposedPageCountByPriority[8], ModifiedPageCountPageFile
        const int fields = 22;
        int size = fields * IntPtr.Size;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQuerySystemInformation(SystemMemoryListInformation, buf, size, out _) != 0) return null;
            ulong page = (ulong)Environment.SystemPageSize;
            ulong Read(int i) => (ulong)Marshal.ReadIntPtr(buf, i * IntPtr.Size).ToInt64() * page;
            ulong standby = 0;
            for (int i = 5; i < 13; i++) standby += Read(i);
            return new MemoryLists(Read(0) + Read(1), standby, Read(2));
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public static ulong GetNonpagedPoolBytes()
    {
        if (!GetPerformanceInfo(out var pi, Marshal.SizeOf<PERFORMANCE_INFORMATION>())) return 0;
        return (ulong)pi.KernelNonpaged * (ulong)pi.PageSize;
    }

    public static string Gb(ulong bytes) => $"{bytes / 1024d / 1024 / 1024:0.0} ГБ";
    public static string Mb(ulong bytes) => $"{bytes / 1024d / 1024:0} МБ";
}
