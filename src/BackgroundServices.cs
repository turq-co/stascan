using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace StaScan;

enum PauseMode { Temporary, Permanent }

/// <summary>Фоновая служба, задача или настройка Windows, которую можно приостановить и потом вернуть.</summary>
abstract class BgItem
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    public abstract bool Exists();
    /// <summary>Текущее состояние для показа в окне.</summary>
    public abstract string Describe();
    /// <summary>Исходное состояние в виде строки, чтобы потом вернуть как было.</summary>
    public abstract string Capture();
    public abstract void Apply(PauseMode mode);
    public abstract void Restore(string original);
    /// <summary>Отключено ли сейчас (кем угодно: StaScan или вручную в Windows).</summary>
    public abstract bool IsOff();
    /// <summary>Включить то, что отключили не через StaScan: ставит значение Windows по умолчанию.</summary>
    public abstract void EnableDefault();
}

// ── Служба ─────────────────────────────────────────────────────────────────────────

sealed class ServiceItem : BgItem
{
    public required string ServiceName { get; init; }
    /// <summary>Тип запуска в чистой Windows: 2 = автоматически, 3 = вручную.</summary>
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
        0 or 1 => "при загрузке",
        2 => "автозапуск",
        3 => "вручную",
        4 => "отключена",
        _ => "?",
    };

    T WithService<T>(uint access, Func<IntPtr, T> action)
    {
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new InvalidOperationException($"нет доступа к диспетчеру служб ({Marshal.GetLastWin32Error()})");
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
        $"{(IsRunning() ? "работает" : "остановлена")} · {StartText(StartType())}";

    public override string Capture() => $"{StartType()}|{(IsRunning() ? 1 : 0)}";

    public override void Apply(PauseMode mode)
    {
        // В обоих режимах служба отключается: иначе Windows поднимет её по триггеру прямо во время игры.
        // Временный режим отличается тем, что StaScan сам вернёт её после перезагрузки.
        WithService(SERVICE_CHANGE_CONFIG | SERVICE_STOP | SERVICE_QUERY_STATUS, h =>
        {
            if (!ChangeServiceConfigW(h, SERVICE_NO_CHANGE, DISABLED, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                throw new InvalidOperationException("Windows не дал изменить тип запуска: " + Win32Text(Marshal.GetLastWin32Error()));

            if (QueryServiceStatus(h, out var st) && st.CurrentState != SERVICE_STOPPED)
            {
                if (!ControlService(h, SERVICE_CONTROL_STOP, out _))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1062) // уже остановлена
                        throw new InvalidOperationException("отключена, но не остановилась: " + Win32Text(err));
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
        uint start = uint.Parse(parts[0]);
        bool wasRunning = parts.Length > 1 && parts[1] == "1";
        WithService(SERVICE_CHANGE_CONFIG | SERVICE_START | SERVICE_QUERY_STATUS, h =>
        {
            if (!ChangeServiceConfigW(h, SERVICE_NO_CHANGE, start, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                throw new InvalidOperationException("не удалось вернуть тип запуска: " + Win32Text(Marshal.GetLastWin32Error()));
            if (wasRunning && QueryServiceStatus(h, out var st) && st.CurrentState != SERVICE_RUNNING)
            {
                if (!StartServiceW(h, 0, null))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1056) // уже запущена
                        Log.Write($"{ServiceName}: тип запуска возвращён, но служба не стартовала: {Win32Text(err)}");
                }
            }
            return 0;
        });
    }

    public override bool IsOff() => StartType() == DISABLED;

    public override void EnableDefault() => Restore($"{DefaultStart}|{(DefaultStart == 2 ? 1 : 0)}");

    static string Win32Text(int code) => $"{new System.ComponentModel.Win32Exception(code).Message} ({code})";
}

// ── Задача планировщика ────────────────────────────────────────────────────────────

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
        if (t == null) return "нет в системе";
        bool enabled = t.Enabled;
        int state = t.State; // 4 = выполняется
        return (enabled ? "включена" : "отключена") + (state == 4 ? ", выполняется сейчас" : "");
    }

    public override string Capture() => GetTask()?.Enabled == true ? "1" : "0";

    public override void Apply(PauseMode mode)
    {
        var t = GetTask() ?? throw new InvalidOperationException("задача не найдена");
        try
        {
            if ((int)t.State == 4) t.Stop(0);
            t.Enabled = false;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Windows не дал отключить задачу (она защищена системой): " + ex.Message);
        }
    }

    public override void Restore(string original)
    {
        var t = GetTask() ?? throw new InvalidOperationException("задача не найдена");
        t.Enabled = original == "1";
    }

    public override bool IsOff() => GetTask()?.Enabled == false;

    public override void EnableDefault() => Restore("1");
}

// ── Значения реестра ───────────────────────────────────────────────────────────────

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

    public override string Describe() =>
        Values.All(v => Equals(Read(v)?.ToString(), v.Target.ToString())) ? OffText : OnText;

    public override string Capture() =>
        JsonSerializer.Serialize(Values.Select(v => Read(v)?.ToString()).ToArray());

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
            else k.SetValue(v.Name, v.Kind == RegistryValueKind.DWord ? int.Parse(o) : o, v.Kind);
        }
    }

    public override bool IsOff() => Describe() == OffText;

    // без этих значений Windows ведёт себя по умолчанию (всё включено)
    public override void EnableDefault() =>
        Restore(JsonSerializer.Serialize(new string?[Values.Length]));
}

// ── Пауза Windows Update (то же, что кнопка «Приостановить обновления» в Параметрах) ──

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
        // формат ISO 8601 разбираем инвариантно: на системах с другой локалью разделители даты и времени отличаются
        if (k?.GetValue("PauseUpdatesExpiryTime") is string s && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var until)
            && until > DateTime.UtcNow)
            return until;
        return null;
    }

    public override string Describe() =>
        CurrentPauseUntilUtc() is { } until ? $"на паузе до {until.ToLocalTime():dd.MM.yyyy}" : "обновления не на паузе";

    public override string Capture()
    {
        using var k = Registry.LocalMachine.OpenSubKey(Key);
        return JsonSerializer.Serialize(Names.Select(n => k?.GetValue(n) as string).ToArray());
    }

    public override void Apply(PauseMode mode)
    {
        var now = DateTime.UtcNow;
        var until = now.AddDays(mode == PauseMode.Temporary ? 7 : 35); // 35 дней — максимум, который даёт Windows
        if (CurrentPauseUntilUtc() >= until) return; // уже стоит пауза длиннее: не сокращаем её
        string F(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
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

// ── Каталог и состояние ────────────────────────────────────────────────────────────

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
    const string GUpdates = "Обновления и индексация";
    const string GTelemetry = "Телеметрия и обслуживание";
    const string GOther = "Прочее";

    public static readonly IReadOnlyList<BgItem> Catalog = new BgItem[]
    {
        new UpdatePauseItem { Id = "wu-pause", Group = GUpdates, Title = "Пауза обновлений Windows",
            Description = "Как «Приостановить обновления» в Параметрах: 7 дней на время игры, 35 насовсем" },
        new ServiceItem { Id = "svc-wuauserv", ServiceName = "wuauserv", Group = GUpdates, Title = "Центр обновления Windows (wuauserv)",
            Description = "Поиск, скачивание и установка обновлений. Служба WaaSMedic может включить её обратно" },
        new ServiceItem { Id = "svc-usosvc", ServiceName = "UsoSvc", DefaultStart = 2, Group = GUpdates, Title = "Оркестратор обновлений (UsoSvc)",
            Description = "Запускает проверку и установку обновлений по расписанию" },
        new ServiceItem { Id = "svc-bits", ServiceName = "BITS", Group = GUpdates, Title = "Фоновая передача (BITS)",
            Description = "Фоновое скачивание обновлений Windows, Edge, Office и т.п." },
        new ServiceItem { Id = "svc-dosvc", ServiceName = "DoSvc", DefaultStart = 2, Group = GUpdates, Title = "Оптимизация доставки (DoSvc)",
            Description = "Раздаёт скачанные обновления другим ПК и грузит сеть" },
        new ServiceItem { Id = "svc-wsearch", ServiceName = "WSearch", DefaultStart = 2, Group = GUpdates, Title = "Индексация поиска (WSearch)",
            Description = "Индексирует файлы для поиска Windows, нагружает диск и процессор" },

        new ServiceItem { Id = "svc-diagtrack", ServiceName = "DiagTrack", DefaultStart = 2, Group = GTelemetry, Title = "Телеметрия (DiagTrack)",
            Description = "Сбор и отправка диагностических данных в Microsoft" },
        new TaskItem { Id = "task-appraiser", TaskPath = @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser", Group = GTelemetry,
            Title = "Проверка совместимости (Compatibility Appraiser)", Description = "Периодически сканирует программы, может надолго грузить CPU и диск" },
        new TaskItem { Id = "task-appraiser-exp", TaskPath = @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser Exp", Group = GTelemetry,
            Title = "Проверка совместимости (Appraiser Exp)", Description = "Вариант той же задачи в новых сборках Windows" },
        new TaskItem { Id = "task-ceip", TaskPath = @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator", Group = GTelemetry,
            Title = "Программа улучшения качества (CEIP)", Description = "Сбор статистики использования" },
        new TaskItem { Id = "task-diskdiag", TaskPath = @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector", Group = GTelemetry,
            Title = "Сбор диагностики дисков", Description = "Отправка данных о дисках" },
        new TaskItem { Id = "task-defrag", TaskPath = @"\Microsoft\Windows\Defrag\ScheduledDefrag", Group = GTelemetry,
            Title = "Оптимизация дисков по расписанию", Description = "Дефрагментация HDD и TRIM для SSD. Насовсем отключать не советую: TRIM полезен SSD" },
        new RegistryItem { Id = "reg-maintenance", Group = GTelemetry, Title = "Автоматическое обслуживание",
            Description = "Ночные и простойные задачи Windows (сканирование, очистка, обновления магазина)",
            Values = new[] { new RegValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "MaintenanceDisabled", 1) },
            OnText = "включено", OffText = "отключено" },

        new ServiceItem { Id = "svc-sysmain", ServiceName = "SysMain", DefaultStart = 2, Group = GOther, Title = "SysMain (Superfetch)",
            Description = "Заранее грузит программы в ОЗУ. На SSD пользы мало, эффект на FPS спорный" },
        new RegistryItem { Id = "reg-gamedvr", Group = GOther, Title = "Фоновая запись Xbox Game Bar",
            Description = "«Записать то, что произошло»: постоянно пишет игру в фоне и ест FPS",
            Values = new[]
            {
                new RegValue(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
                new RegValue(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0),
            },
            OnText = "включена", OffText = "отключена" },
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
            Log.Write($"background-state.json не прочитан: {ex.Message}");
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
            Log.Write($"Не удалось сохранить background-state.json: {ex.Message}");
        }
    }

    /// <summary>Приостанавливает выбранные пункты. Возвращает (успешно, ошибки).</summary>
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
                    // исходное состояние запоминаем только один раз, чтобы повторная пауза не перезаписала его
                    var original = entry?.Original ?? item.Capture();
                    item.Apply(mode);
                    if (entry == null)
                    {
                        entry = new BgEntry { Id = item.Id, Original = original };
                        state.Add(entry);
                    }
                    if (entry.Mode != PauseMode.Permanent) entry.Mode = mode; // «насовсем» не понижаем до временного
                    entry.BootTimeUtc = Baseline.CurrentBootUtc;
                    entry.At = DateTime.Now;
                    ok++;
                    Log.Write($"Фон: {item.Title} → {(mode == PauseMode.Temporary ? "на время игры" : "насовсем")}");
                }
                catch (Exception ex)
                {
                    errors.Add($"{item.Title}: {ex.Message}");
                    Log.Write($"Фон: {item.Title} не изменён: {ex.Message}");
                }
            }
            SaveState(state);
            RaiseChanged();
            return (ok, errors);
        }
    }

    /// <summary>Возвращает всё как было. onlyTemporaryFromOldBoot — для автоотката после перезагрузки.</summary>
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
                    Log.Write($"Фон: {item.Title} возвращено как было");
                }
                catch (Exception ex)
                {
                    errors.Add($"{item.Title}: {ex.Message}");
                    Log.Write($"Фон: {item.Title} не удалось вернуть: {ex.Message}");
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
        return e.Mode == PauseMode.Temporary ? $"на время игры (с {e.At:HH:mm})" : $"насовсем (с {e.At:dd.MM})";
    }

    /// <summary>Срабатывает после любого изменения (из окна, из трея, из подготовки). Вызывается не из UI-потока.</summary>
    public static event Action? Changed;

    static void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Write($"Обработчик BackgroundServices.Changed упал: {ex.Message}"); }
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
    /// Переключатель одного пункта: работает → на время игры; приостановлено StaScan → как было;
    /// отключено вручную в Windows → включить со значением Windows по умолчанию.
    /// Возвращает текст для уведомления, при ошибке бросает исключение.
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
                return $"{item.Title}: возвращено как было";
            }
            case BgStatus.OffInWindows:
                lock (Sync) item.EnableDefault();
                Log.Write($"Фон: {item.Title} включено со значением Windows по умолчанию");
                RaiseChanged();
                return $"{item.Title}: включено (как в Windows по умолчанию)";
            default:
            {
                var (_, errors) = Apply(new[] { id }, PauseMode.Temporary);
                if (errors.Count > 0) throw new InvalidOperationException(errors[0]);
                return $"{item.Title}: приостановлено на время игры";
            }
        }
    }

    public static string StatusText(BgStatus s) => s switch
    {
        BgStatus.PausedForGame => "на время игры",
        BgStatus.DisabledPermanently => "отключено насовсем",
        BgStatus.OffInWindows => "отключено в Windows",
        _ => "работает",
    };
}

enum BgStatus { Active, PausedForGame, DisabledPermanently, OffInWindows }
