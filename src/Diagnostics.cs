using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StaScan;

/// <summary>One memory sample.</summary>
readonly record struct MemorySample(DateTime At, ulong Free, ulong Standby, ulong Modified, ulong Nonpaged, ulong Commit);

/// <summary>
/// Samples system memory once a minute and keeps the last 24 hours, so a slow drift (a driver leak,
/// a growing standby list) is visible without having to reproduce it. Costs a few KB and one cheap query a minute.
/// </summary>
sealed class MetricsHistory : IDisposable
{
    const int Capacity = 24 * 60;
    readonly Queue<MemorySample> _samples = new();
    readonly System.Threading.Timer _timer;

    /// <summary>Nonpaged pool usage per tag when StaScan started, to show growth.</summary>
    public IReadOnlyDictionary<string, ulong> PoolAtStart { get; private set; } = new Dictionary<string, ulong>();
    public DateTime StartedAt { get; } = DateTime.Now;

    public MetricsHistory()
    {
        _timer = new System.Threading.Timer(_ => Sample(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
        Task.Run(() =>
        {
            try { PoolAtStart = PoolTags.Query().ToDictionary(t => t.Tag, t => t.NonpagedBytes); }
            catch (Exception ex) { Log.Write($"Pool tag snapshot failed: {ex.Message}"); }
        });
    }

    public static MemorySample? Now()
    {
        Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
        var lists = Native.QueryMemoryLists();
        var perf = Native.GetPerf();
        if (lists == null || perf == null) return null;
        return new MemorySample(DateTime.Now, lists.Value.FreeBytes, lists.Value.StandbyBytes, lists.Value.ModifiedBytes,
            perf.Value.NonpagedBytes, perf.Value.CommitBytes);
    }

    void Sample()
    {
        if (Now() is not { } s) return;
        lock (_samples)
        {
            _samples.Enqueue(s);
            while (_samples.Count > Capacity) _samples.Dequeue();
        }
    }

    public List<MemorySample> Snapshot()
    {
        lock (_samples) return _samples.ToList();
    }

    /// <summary>Nonpaged pool growth rate in MB per hour over the recorded history (needs at least 30 minutes).</summary>
    public double? NonpagedGrowthMbPerHour()
    {
        var s = Snapshot();
        if (s.Count < 2) return null;
        var span = s[^1].At - s[0].At;
        if (span < TimeSpan.FromMinutes(30)) return null;
        return ((double)s[^1].Nonpaged - s[0].Nonpaged) / 1048576.0 / span.TotalHours;
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>Kernel pool usage per allocation tag (what poolmon shows).</summary>
static class PoolTags
{
    public readonly record struct Entry(string Tag, ulong NonpagedBytes, ulong PagedBytes, uint NonpagedAllocs, uint NonpagedFrees);

    const int SystemPoolTagInformation = 22;
    const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    public static List<Entry> Query()
    {
        int size = 512 * 1024;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemPoolTagInformation, buf, size, out int needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + 64 * 1024);
                    continue;
                }
                if (status != 0) throw new InvalidOperationException($"NTSTATUS 0x{status:X8}");

                // SYSTEM_POOLTAG_INFORMATION: ULONG Count; SYSTEM_POOLTAG TagInfo[] (8-byte aligned, 40 bytes each on x64)
                int count = Marshal.ReadInt32(buf);
                var result = new List<Entry>(count);
                var tagBytes = new byte[4];
                for (int i = 0; i < count; i++)
                {
                    IntPtr e = buf + 8 + i * 40;
                    Marshal.Copy(e, tagBytes, 0, 4);
                    var tag = Encoding.ASCII.GetString(tagBytes);
                    ulong pagedUsed = (ulong)Marshal.ReadInt64(e, 16);
                    uint npAllocs = (uint)Marshal.ReadInt32(e, 24);
                    uint npFrees = (uint)Marshal.ReadInt32(e, 28);
                    ulong npUsed = (ulong)Marshal.ReadInt64(e, 32);
                    result.Add(new Entry(tag, npUsed, pagedUsed, npAllocs, npFrees));
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        throw new InvalidOperationException("pool tag buffer kept growing");
    }

    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool EnumDeviceDrivers([Out] IntPtr[] imageBases, int size, out int needed);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    static extern int GetDeviceDriverFileNameW(IntPtr imageBase, StringBuilder fileName, int size);

    /// <summary>Paths of the kernel drivers that are currently loaded.</summary>
    public static List<string> LoadedDrivers()
    {
        var bases = new IntPtr[1024];
        if (!EnumDeviceDrivers(bases, bases.Length * IntPtr.Size, out int needed)) return new();
        int count = Math.Min(bases.Length, needed / IntPtr.Size);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var result = new List<string>();
        var sb = new StringBuilder(1024);
        for (int i = 0; i < count; i++)
        {
            if (GetDeviceDriverFileNameW(bases[i], sb, sb.Capacity) == 0) continue;
            var path = sb.ToString();
            if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) path = Path.Combine(windows, path[12..]);
            else if (path.StartsWith(@"\??\")) path = path[4..];
            else if (path.StartsWith(@"\Windows\", StringComparison.OrdinalIgnoreCase)) path = Path.Combine(Path.GetPathRoot(windows)!, path[1..]);
            if (File.Exists(path)) result.Add(path);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Finds loaded drivers whose file contains the tag as text, the same trick as "findstr /m /l TAG *.sys".
    /// A tag can appear in several drivers, so the result is a list of candidates.
    /// </summary>
    public static List<string> FindDrivers(string tag)
    {
        var needle = Encoding.ASCII.GetBytes(tag);
        var hits = new List<string>();
        foreach (var file in LoadedDrivers())
        {
            if (FileContains(file, needle)) hits.Add(file);
        }
        return hits;
    }

    /// <summary>Streams the file in chunks so large drivers (100+ MB) do not have to be loaded into memory.</summary>
    static bool FileContains(string path, byte[] needle)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var buf = new byte[(1 << 20) + needle.Length];
            int carry = 0, read;
            while ((read = fs.Read(buf, carry, buf.Length - carry)) > 0)
            {
                int len = carry + read;
                if (buf.AsSpan(0, len).IndexOf(needle) >= 0) return true;
                carry = Math.Min(needle.Length - 1, len);
                Buffer.BlockCopy(buf, len - carry, buf, 0, carry);
            }
        }
        catch
        {
            // file in use or access denied
        }
        return false;
    }
}

/// <summary>Dedicated GPU memory per process, from the "GPU Process Memory" performance counters.</summary>
static class GpuMemory
{
    const uint PDH_FMT_LARGE = 0x00000400;
    const uint PDH_MORE_DATA = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQueryW(string? source, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);

    public readonly record struct Entry(int Pid, string Name, ulong DedicatedBytes);

    /// <summary>Processes using dedicated video memory, largest first. Empty if the counters are unavailable.</summary>
    public static List<Entry> Query()
    {
        var perPid = new Dictionary<int, ulong>();
        if (PdhOpenQueryW(null, IntPtr.Zero, out var query) != 0) return new();
        try
        {
            // the English counter path works on every Windows display language
            if (PdhAddEnglishCounterW(query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out var counter) != 0) return new();
            if (PdhCollectQueryData(query) != 0) return new();

            uint size = 0;
            uint status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, ref size, out _, IntPtr.Zero);
            if (status != PDH_MORE_DATA || size == 0) return new();
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, ref size, out uint count, buf) != 0) return new();
                // PDH_FMT_COUNTERVALUE_ITEM_W: LPWSTR name; { DWORD CStatus; LONGLONG value } (24 bytes on x64)
                int itemSize = IntPtr.Size == 8 ? 24 : 16;
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = buf + i * itemSize;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    long value = Marshal.ReadInt64(item, IntPtr.Size == 8 ? 16 : 8);
                    // instance names look like "pid_1234_luid_0x00000000_0x0000C2E1_phys_0"
                    var m = System.Text.RegularExpressions.Regex.Match(name, @"^pid_(\d+)_");
                    if (!m.Success || value <= 0) continue;
                    int pid = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    perPid[pid] = perPid.GetValueOrDefault(pid) + (ulong)value;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            PdhCloseQuery(query);
        }

        var result = new List<Entry>();
        foreach (var (pid, bytes) in perPid)
        {
            string name;
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName + ".exe"; }
            catch { name = $"PID {pid} (exited)"; }
            result.Add(new Entry(pid, name, bytes));
        }
        return result.OrderByDescending(e => e.DedicatedBytes).ToList();
    }
}
