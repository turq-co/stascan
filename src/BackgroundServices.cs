using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace StaScan;

enum PauseMode { Temporary, Permanent }

/// <summary>A Windows background service, task or setting that can be paused and later restored.</summary>
abstract class BgItem
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    public abstract bool Exists();
    /// <summary>Current state, for display.</summary>
    public abstract string Describe();
    /// <summary>The original state as a string, so it can be restored exactly.</summary>
    public abstract string Capture();
    public abstract void Apply(PauseMode mode);
    public abstract void Restore(string original);
    /// <summary>Whether it is currently off (by StaScan or manually in Windows).</summary>
    public abstract bool IsOff();
    /// <summary>Turns on something that was disabled outside StaScan, using the Windows default.</summary>
    public abstract void EnableDefault();
}

// ── Service ───────────────────────────────────────────────────────────────────────

sealed class ServiceItem : BgItem
{
    public required string ServiceName { get; init; }
    /// <summary>Start type in a clean Windows install: 2 = automatic, 3 = manual.</summary>
    public uint DefaultStart { get; init; } = 3;

    const uint SC_MANAGER_CONNECT = 0x1;
    const uint SERVICE_QUERY_STATUS = 0x4, SERVICE_CHANGE_CONFIG = 0x2, SERVICE_START = 0x10, SERVICE_STOP = 0x20;
    const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    const uint SERVICE_CONTROL_STOP = 0x1;
    const uint SERVICE_STOPPED = 1, SERVICE_RUNNING = 4;
    const uint DISABLED = 4;

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS
    {
        public uint Type, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CloseServiceHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatus(IntPtr h, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool ControlService(IntPtr h, uint control, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool StartServiceW(IntPtr h, int argc, string[]? argv);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ChangeServiceConfigW(IntPtr h, uint type, uint start, uint error, string? path,
        string? group, IntPtr tag, string? deps, string? user, string? password, string? displayName);

    string RegPath => $@"SYSTEM\CurrentControlSet\Services\{ServiceName}";

    public override bool Exists()
    {
        using var k = Registry.LocalMachine.OpenSubKey(RegPath);
        return k != null;
    }

    int StartType()
    {
        using var k = Registry.LocalMachine.OpenSubKey(RegPath);
        return k?.GetValue("Start") is int v ? v : -1;
    }

    static string StartText(int s) => s switch
    {
        0 or 1 => L.T("boot"),
        2 => L.T("automatic"),
        3 => L.T("manual"),
        4 => L.T("disabled"),
        _ => "?",
    };

    T WithService<T>(uint access, Func<IntPtr, T> action)
    {
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new InvalidOperationException(L.F("cannot access the Service Control Manager ({0})", Marshal.GetLastWin32Error()));
        try
        {
            IntPtr svc = OpenServiceW(scm, ServiceName, access);
            if (svc == IntPtr.Zero) throw new InvalidOperationException(Win32Text(Marshal.GetLastWin32Error()));
            try { return action(svc); }
            finally { CloseServiceHandle(svc); }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    bool IsRunning()
    {
        try
        {
            return WithService(SERVICE_QUERY_STATUS, h =>
                QueryServiceStatus(h, out var st) && st.CurrentState != SERVICE_STOPPED);
        }
        catch
        {
            return false;
        }
    }

    public override string Describe() =>
        $"{(IsRunning() ? L.T("running") : L.T("stopped"))} · {StartText(StartType())}";

    public override string Capture() => $"{StartType()}|{(IsRunning() ? 1 : 0)}";

    public override void Apply(PauseMode mode)
    {
        // The service is disabled in both modes; otherwise Windows would start it again via a trigger mid-game.
        // The temporary mode differs only in that StaScan restores it automatically after a reboot.
        WithService(SERVICE_CHANGE_CONFIG | SERVICE_STOP | SERVICE_QUERY_STATUS, h =>
        {
            if (!ChangeServiceConfigW(h, SERVICE_NO_CHANGE, DISABLED, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                throw new InvalidOperationException(L.F("Windows did not allow changing the start type: {0}", Win32Text(Marshal.GetLastWin32Error())));

            if (QueryServiceStatus(h, out var st) && st.CurrentState != SERVICE_STOPPED)
            {
                if (!ControlService(h, SERVICE_CONTROL_STOP, out _))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1062) // already stopped
                        throw new InvalidOperationException(L.F("disabled, but it did not stop: {0}", Win32Text(err)));
                }
                for (int i = 0; i < 40 && QueryServiceStatus(h, out st) && st.CurrentState != SERVICE_STOPPED; i++)
                    Thread.Sleep(250);
            }
            return 0;
        });
    }

    public override void Restore(string original)
    {
        var parts = original.Split('|');
        uint start = uint.Parse(parts[0], CultureInfo.InvariantCulture);
        bool wasRunning = parts.Length > 1 && parts[1] == "1";
        WithService(SERVICE_CHANGE_CONFIG | SERVICE_START | SERVICE_QUERY_STATUS, h =>
        {
            if (!ChangeServiceConfigW(h, SERVICE_NO_CHANGE, start, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                throw new InvalidOperationException(L.F("could not restore the start type: {0}", Win32Text(Marshal.GetLastWin32Error())));
            if (wasRunning && QueryServiceStatus(h, out var st) && st.CurrentState != SERVICE_RUNNING)
            {
                if (!StartServiceW(h, 0, null))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1056) // already running
                        Log.Write($"{ServiceName}: start type restored, but the service did not start: {Win32Text(err)}");
                }
            }
            return 0;
        });
    }

    public override bool IsOff() => StartType() == DISABLED;

    public override void EnableDefault() => Restore($"{DefaultStart}|{(DefaultStart == 2 ? 1 : 0)}");

    static string Win32Text(int code) => $"{new System.ComponentModel.Win32Exception(code).Message} ({code})";
}

// ── Scheduled task ────────────────────────────────────────────────────────────────

sealed class TaskItem : BgItem
{
    public required string TaskPath { get; init; }

    dynamic? GetTask()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return null;
            dynamic svc = Activator.CreateInstance(type)!;
            svc.Connect();
            int i = TaskPath.LastIndexOf('\\');
            string folder = i <= 0 ? "\\" : TaskPath[..i];
            return svc.GetFolder(folder).GetTask(TaskPath[(i + 1)..]);
        }
        catch
        {
            return null;
        }
    }

    public override bool Exists() => GetTask() != null;

    public override string Describe()
    {
        var t = GetTask();
        if (t == null) return L.T("not present");
        bool enabled = t.Enabled;
        int state = t.State; // 4 = running
        return (enabled ? L.T("enabled") : L.T("disabled")) + (state == 4 ? L.T(", running now") : "");
    }

    public override string Capture() => GetTask()?.Enabled == true ? "1" : "0";

    public override void Apply(PauseMode mode)
    {
        var t = GetTask() ?? throw new InvalidOperationException(L.T("task not found"));
        try
        {
            if ((int)t.State == 4) t.Stop(0);
            t.Enabled = false;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(L.F("Windows did not allow disabling the task (it is protected by the system): {0}", ex.Message));
        }
    }

    public override void Restore(string original)
    {
        var t = GetTask() ?? throw new InvalidOperationException(L.T("task not found"));
        t.Enabled = original == "1";
    }

    public override bool IsOff() => GetTask()?.Enabled == false;

    public override void EnableDefault() => Restore("1");
}

// ── Registry values ───────────────────────────────────────────────────────────────

sealed record RegValue(RegistryKey Hive, string Key, string Name, object Target, RegistryValueKind Kind = RegistryValueKind.DWord);

sealed class RegistryItem : BgItem
{
    public required RegValue[] Values { get; init; }
    public required string OnText { get; init; }
    public required string OffText { get; init; }

    public override bool Exists() => true;

    static object? Read(RegValue v)
    {
        using var k = v.Hive.OpenSubKey(v.Key);
        return k?.GetValue(v.Name);
    }

    bool AllAtTarget() =>
        Values.All(v => Equals(Convert.ToString(Read(v), CultureInfo.InvariantCulture), Convert.ToString(v.Target, CultureInfo.InvariantCulture)));

    public override string Describe() => AllAtTarget() ? L.T(OffText) : L.T(OnText);

    public override string Capture() =>
        JsonSerializer.Serialize(Values.Select(v => Read(v) is { } o ? Convert.ToString(o, CultureInfo.InvariantCulture) : null).ToArray());

    public override void Apply(PauseMode mode)
    {
        foreach (var v in Values)
        {
            using var k = v.Hive.CreateSubKey(v.Key, writable: true);
            k.SetValue(v.Name, v.Target, v.Kind);
        }
    }

    public override void Restore(string original)
    {
        var old = JsonSerializer.Deserialize<string?[]>(original) ?? Array.Empty<string?>();
        for (int i = 0; i < Values.Length; i++)
        {
            var v = Values[i];
            using var k = v.Hive.CreateSubKey(v.Key, writable: true);
            var o = i < old.Length ? old[i] : null;
            if (o == null) k.DeleteValue(v.Name, throwOnMissingValue: false);
            else k.SetValue(v.Name, v.Kind == RegistryValueKind.DWord ? int.Parse(o, CultureInfo.InvariantCulture) : o, v.Kind);
        }
    }

    public override bool IsOff() => AllAtTarget();

    // without these values Windows uses its defaults (everything on)
    public override void EnableDefault() =>
        Restore(JsonSerializer.Serialize(new string?[Values.Length]));
}

// ── Windows Update pause (same as "Pause updates" in Settings) ───────────────────

sealed class UpdatePauseItem : BgItem
{
    const string Key = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    static readonly string[] Names =
    {
        "PauseUpdatesStartTime", "PauseUpdatesExpiryTime",
        "PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime",
        "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime",
    };

    public override bool Exists() => true;

    static DateTime? CurrentPauseUntilUtc()
    {
        using var k = Registry.LocalMachine.OpenSubKey(Key);
        // parse ISO 8601 with the invariant culture: date and time separators differ between locales
        if (k?.GetValue("PauseUpdatesExpiryTime") is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until)
            && until > DateTime.UtcNow)
            return until;
        return null;
    }

    public override string Describe() =>
        CurrentPauseUntilUtc() is { } until
            ? L.F("paused until {0}", L.Date(until.ToLocalTime()))
            : L.T("updates are not paused");

    public override string Capture()
    {
        using var k = Registry.LocalMachine.OpenSubKey(Key);
        return JsonSerializer.Serialize(Names.Select(n => k?.GetValue(n) as string).ToArray());
    }

    public override void Apply(PauseMode mode)
    {
        var now = DateTime.UtcNow;
        var until = now.AddDays(mode == PauseMode.Temporary ? 7 : 35); // 35 days is the maximum Windows allows
        if (CurrentPauseUntilUtc() >= until) return; // a longer pause is already set: do not shorten it
        string F(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        using var k = Registry.LocalMachine.CreateSubKey(Key, writable: true);
        k.SetValue("PauseUpdatesStartTime", F(now));
        k.SetValue("PauseUpdatesExpiryTime", F(until));
        k.SetValue("PauseFeatureUpdatesStartTime", F(now));
        k.SetValue("PauseFeatureUpdatesEndTime", F(until));
        k.SetValue("PauseQualityUpdatesStartTime", F(now));
        k.SetValue("PauseQualityUpdatesEndTime", F(until));
    }

    public override void Restore(string original)
    {
        var old = JsonSerializer.Deserialize<string?[]>(original) ?? Array.Empty<string?>();
        using var k = Registry.LocalMachine.CreateSubKey(Key, writable: true);
        for (int i = 0; i < Names.Length; i++)
        {
            var o = i < old.Length ? old[i] : null;
            if (o == null) k.DeleteValue(Names[i], throwOnMissingValue: false);
            else k.SetValue(Names[i], o);
        }
    }

    public override bool IsOff() => CurrentPauseUntilUtc() != null;

    public override void EnableDefault() => Restore(JsonSerializer.Serialize(new string?[Names.Length]));
}

// ── Catalog and state ─────────────────────────────────────────────────────────────

sealed class BgEntry
{
    public required string Id { get; set; }
    public PauseMode Mode { get; set; }
    public required string Original { get; set; }
    public DateTime BootTimeUtc { get; set; }
    public DateTime At { get; set; }
}

static class BackgroundServices
{
    const string GUpdates = "Updates and indexing";
    const string GTelemetry = "Telemetry and maintenance";
    const string GOther = "Other";

    public static readonly IReadOnlyList<BgItem> Catalog = new BgItem[]
    {
        new UpdatePauseItem { Id = "wu-pause", Group = GUpdates, Title = "Windows Update pause",
            Description = "Same as \"Pause updates\" in Settings: 7 days for a gaming session, 35 days permanently" },
        new ServiceItem { Id = "svc-wuauserv", ServiceName = "wuauserv", Group = GUpdates, Title = "Windows Update (wuauserv)",
            Description = "Finds, downloads and installs updates. WaaSMedic may turn it back on" },
        new ServiceItem { Id = "svc-usosvc", ServiceName = "UsoSvc", DefaultStart = 2, Group = GUpdates, Title = "Update Orchestrator (UsoSvc)",
            Description = "Schedules update scans and installations" },
        new ServiceItem { Id = "svc-bits", ServiceName = "BITS", Group = GUpdates, Title = "Background Intelligent Transfer (BITS)",
            Description = "Background downloads for Windows, Edge, Office and more" },
        new ServiceItem { Id = "svc-dosvc", ServiceName = "DoSvc", DefaultStart = 2, Group = GUpdates, Title = "Delivery Optimization (DoSvc)",
            Description = "Shares downloaded updates with other PCs and uses bandwidth" },
        new ServiceItem { Id = "svc-wsearch", ServiceName = "WSearch", DefaultStart = 2, Group = GUpdates, Title = "Windows Search indexing (WSearch)",
            Description = "Indexes files for Windows Search, loading the disk and CPU" },

        new ServiceItem { Id = "svc-diagtrack", ServiceName = "DiagTrack", DefaultStart = 2, Group = GTelemetry, Title = "Telemetry (DiagTrack)",
            Description = "Collects and sends diagnostic data to Microsoft" },
        new TaskItem { Id = "task-appraiser", TaskPath = @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser", Group = GTelemetry,
            Title = "Compatibility Appraiser", Description = "Periodically scans installed programs and can load the CPU and disk for a long time" },
        new TaskItem { Id = "task-appraiser-exp", TaskPath = @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser Exp", Group = GTelemetry,
            Title = "Compatibility Appraiser (Exp)", Description = "Variant of the same task in newer Windows builds" },
        new TaskItem { Id = "task-ceip", TaskPath = @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator", Group = GTelemetry,
            Title = "Customer Experience Improvement Program", Description = "Collects usage statistics" },
        new TaskItem { Id = "task-diskdiag", TaskPath = @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector", Group = GTelemetry,
            Title = "Disk diagnostics collector", Description = "Sends disk diagnostic data" },
        new TaskItem { Id = "task-defrag", TaskPath = @"\Microsoft\Windows\Defrag\ScheduledDefrag", Group = GTelemetry,
            Title = "Scheduled drive optimization", Description = "HDD defragmentation and SSD TRIM. Not recommended to disable permanently: SSDs benefit from TRIM" },
        new RegistryItem { Id = "reg-maintenance", Group = GTelemetry, Title = "Automatic Maintenance",
            Description = "Windows idle and nightly tasks (scans, cleanup, Store updates)",
            Values = new[] { new RegValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "MaintenanceDisabled", 1) },
            OnText = "enabled", OffText = "disabled" },

        new ServiceItem { Id = "svc-sysmain", ServiceName = "SysMain", DefaultStart = 2, Group = GOther, Title = "SysMain (Superfetch)",
            Description = "Preloads programs into RAM. Little benefit on SSDs; effect on FPS is debatable" },
        new RegistryItem { Id = "reg-gamedvr", Group = GOther, Title = "Xbox Game Bar background recording",
            Description = "\"Record what happened\": constantly records gameplay in the background and costs FPS",
            Values = new[]
            {
                new RegValue(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
                new RegValue(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0),
            },
            OnText = "enabled", OffText = "disabled" },
    };

    public static IEnumerable<BgItem> Available => Catalog.Where(i => { try { return i.Exists(); } catch { return false; } });

    static string StatePath => AppPaths.File("background-state.json");
    static readonly object Sync = new();

    public static List<BgEntry> LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<List<BgEntry>>(File.ReadAllText(StatePath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read background-state.json: {ex.Message}");
        }
        return new();
    }

    static void SaveState(List<BgEntry> state)
    {
        try
        {
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write($"Could not save background-state.json: {ex.Message}");
        }
    }

    /// <summary>Pauses the selected items. Returns (succeeded, errors).</summary>
    public static (int ok, List<string> errors) Apply(IEnumerable<string> ids, PauseMode mode)
    {
        lock (Sync)
        {
            var state = LoadState();
            var errors = new List<string>();
            int ok = 0;
            foreach (var item in Available.Where(i => ids.Contains(i.Id)))
            {
                var entry = state.FirstOrDefault(e => e.Id == item.Id);
                try
                {
                    // capture the original state only once so a repeated pause does not overwrite it
                    var original = entry?.Original ?? item.Capture();
                    item.Apply(mode);
                    if (entry == null)
                    {
                        entry = new BgEntry { Id = item.Id, Original = original };
                        state.Add(entry);
                    }
                    if (entry.Mode != PauseMode.Permanent) entry.Mode = mode; // never downgrade "permanent" to temporary
                    entry.BootTimeUtc = Baseline.CurrentBootUtc;
                    entry.At = DateTime.Now;
                    ok++;
                    Log.Write($"Background: {item.Title} → {(mode == PauseMode.Temporary ? "paused for this session" : "disabled permanently")}");
                }
                catch (Exception ex)
                {
                    errors.Add($"{L.T(item.Title)}: {ex.Message}");
                    Log.Write($"Background: {item.Title} not changed: {ex.Message}");
                }
            }
            SaveState(state);
            RaiseChanged();
            return (ok, errors);
        }
    }

    /// <summary>Restores everything. onlyTemporaryFromOldBoot is used for the automatic restore after a reboot.</summary>
    public static (int ok, List<string> errors) Restore(bool onlyTemporaryFromOldBoot = false) =>
        Restore(e => !onlyTemporaryFromOldBoot ||
                     (e.Mode == PauseMode.Temporary && Math.Abs((e.BootTimeUtc - Baseline.CurrentBootUtc).TotalMinutes) >= 2));

    public static (int ok, List<string> errors) RestoreOne(string id) => Restore(e => e.Id == id);

    static (int ok, List<string> errors) Restore(Func<BgEntry, bool> filter)
    {
        lock (Sync)
        {
            var state = LoadState();
            var errors = new List<string>();
            int ok = 0;
            foreach (var entry in state.ToList())
            {
                if (!filter(entry)) continue;

                var item = Catalog.FirstOrDefault(i => i.Id == entry.Id);
                if (item == null)
                {
                    state.Remove(entry);
                    continue;
                }
                try
                {
                    item.Restore(entry.Original);
                    state.Remove(entry);
                    ok++;
                    Log.Write($"Background: {item.Title} restored");
                }
                catch (Exception ex)
                {
                    errors.Add($"{L.T(item.Title)}: {ex.Message}");
                    Log.Write($"Background: could not restore {item.Title}: {ex.Message}");
                }
            }
            SaveState(state);
            RaiseChanged();
            return (ok, errors);
        }
    }

    public static string? PausedMark(BgItem item, List<BgEntry> state)
    {
        var e = state.FirstOrDefault(s => s.Id == item.Id);
        if (e == null) return null;
        return e.Mode == PauseMode.Temporary
            ? L.F("paused for session (since {0})", e.At.ToString("HH:mm", CultureInfo.InvariantCulture))
            : L.F("disabled (since {0})", L.Day(e.At));
    }

    /// <summary>Raised after any change (from the window, the tray or game prep). Not raised on the UI thread.</summary>
    public static event Action? Changed;

    static void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Write($"BackgroundServices.Changed handler failed: {ex.Message}"); }
    }

    public static BgStatus GetStatus(BgItem item, List<BgEntry> state)
    {
        var e = state.FirstOrDefault(s => s.Id == item.Id);
        if (e != null) return e.Mode == PauseMode.Temporary ? BgStatus.PausedForGame : BgStatus.DisabledPermanently;
        bool off;
        try { off = item.IsOff(); } catch { off = false; }
        return off ? BgStatus.OffInWindows : BgStatus.Active;
    }

    /// <summary>
    /// Toggles a single item: running → paused for the session; paused by StaScan → restored;
    /// disabled manually in Windows → enabled with the Windows default.
    /// Returns a notification text; throws on failure.
    /// </summary>
    public static string Toggle(string id)
    {
        var item = Catalog.First(i => i.Id == id);
        switch (GetStatus(item, LoadState()))
        {
            case BgStatus.PausedForGame:
            case BgStatus.DisabledPermanently:
            {
                var (_, errors) = RestoreOne(id);
                if (errors.Count > 0) throw new InvalidOperationException(errors[0]);
                return L.F("{0}: restored", L.T(item.Title));
            }
            case BgStatus.OffInWindows:
                lock (Sync) item.EnableDefault();
                Log.Write($"Background: {item.Title} enabled with the Windows default");
                RaiseChanged();
                return L.F("{0}: enabled (Windows default)", L.T(item.Title));
            default:
            {
                var (_, errors) = Apply(new[] { id }, PauseMode.Temporary);
                if (errors.Count > 0) throw new InvalidOperationException(errors[0]);
                return L.F("{0}: paused for this gaming session", L.T(item.Title));
            }
        }
    }

    public static string StatusText(BgStatus s) => s switch
    {
        BgStatus.PausedForGame => L.T("paused for session"),
        BgStatus.DisabledPermanently => L.T("disabled permanently"),
        BgStatus.OffInWindows => L.T("disabled in Windows"),
        _ => L.T("running"),
    };
}

enum BgStatus { Active, PausedForGame, DisabledPermanently, OffInWindows }
