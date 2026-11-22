using System.Drawing.Drawing2D;
using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace StaScan;

sealed class TrayContext : ApplicationContext
{
    readonly NotifyIcon _tray;
    readonly ContextMenuStrip _menu;
    readonly Settings _settings = Settings.Load();
    readonly AffinityWatcher _affinity;
    readonly AppServices _app;
    readonly ToolStripMenuItem _servicesMenu;
    readonly SynchronizationContext _ui;
    Baseline _baseline;
    MainForm? _main;

    public TrayContext()
    {
        Theme.Set(_settings.Theme);
        (_baseline, bool reused) = Baseline.LoadOrTake();

        _menu = new ContextMenuStrip { Renderer = Theme.MenuRenderer() };
        var prepare = new ToolStripMenuItem("Подготовить к игре…", null, (_, _) => Open(Page.Prepare))
        {
            Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold),
        };
        _servicesMenu = new ToolStripMenuItem("Фоновые службы");
        _servicesMenu.DropDownItems.Add("…"); // заглушка, чтобы появилась стрелка подменю
        _servicesMenu.DropDownOpening += (_, _) => BuildServicesMenu();

        _menu.Items.Add(prepare);
        _menu.Items.Add(_servicesMenu);
        _menu.Items.Add("USB-порты мыши и клавиатуры…", null, (_, _) => Open(Page.Usb));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Открыть StaScan", null, (_, _) => Open(Page.Prepare));
        _menu.Items.Add("Настройки…", null, (_, _) => Open(Page.Settings));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Выход", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = TrayIcon.Create(),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => Open(Page.Prepare);
        UpdateTooltip();

        // уведомления из таймера приходят не из UI-потока
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _affinity = new AffinityWatcher(() => _settings.Cs2SkipCpu0, (t, i) => _ui.Post(_ => Notify(t, i), null));

        _app = new AppServices
        {
            Settings = _settings,
            GetBaseline = () => _baseline,
            RetakeBaseline = RetakeBaseline,
            Notify = Notify,
            Cs2SettingChanged = () => { _affinity.Update(); if (_settings.Cs2SkipCpu0) Task.Run(_affinity.Tick); },
            ChangeTheme = ChangeTheme,
        };

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        Autostart.RefreshIfEnabled();
        _ = AutoRestoreAfterReboot();

        if (!reused && Environment.TickCount64 > TimeSpan.FromMinutes(10).TotalMilliseconds)
        {
            Notify("Снимок сделан сейчас, а не при входе в Windows. Уже открытые программы считаются «чистыми». " +
                   "Включите автозапуск в разделе «Настройки».", ToolTipIcon.Info);
        }

        // после запуска сразу отдаём системе память, занятую инициализацией
        TrimMemory();
    }

    void Open(Page page)
    {
        if (_main is null or { IsDisposed: true })
        {
            _main = new MainForm(_app);
            _main.FormClosed += (_, _) =>
            {
                _main = null;
                TrimMemory();
            };
        }
        _main.Open(page);
    }

    void ChangeTheme(ThemeMode mode, Page reopenAt)
    {
        _settings.Theme = mode;
        _settings.Save();
        ApplyTheme(reopenAt);
    }

    void ApplyTheme(Page? reopenAt)
    {
        Theme.Set(_settings.Theme);
        _menu.Renderer = Theme.MenuRenderer();
        if (_main is { IsDisposed: false } main && !main.IsBusy)
        {
            var bounds = main.Bounds;
            var state = main.WindowState;
            main.Close();
            Open(reopenAt ?? Page.Prepare);
            if (_main != null)
            {
                _main.StartPosition = FormStartPosition.Manual;
                _main.Bounds = bounds;
                _main.WindowState = state;
            }
        }
    }

    /// <summary>Тема «Как в Windows»: подхватываем переключение светлой/тёмной темы в Параметрах.</summary>
    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || _settings.Theme != ThemeMode.System) return;
        if (Theme.SystemIsDark() == Theme.P.IsDark) return;
        _ui.Post(_ => ApplyTheme(Page.Settings), null);
    }

    /// <summary>Подменю «Фоновые службы»: клик по службе переключает её (работает ⇄ на время игры).</summary>
    void BuildServicesMenu()
    {
        var items = _servicesMenu.DropDownItems;
        items.Clear();

        var state = BackgroundServices.LoadState();
        var available = BackgroundServices.Available.ToList();
        string? group = null;
        foreach (var item in available)
        {
            if (item.Group != group)
            {
                if (group != null) items.Add(new ToolStripSeparator());
                group = item.Group;
                items.Add(new ToolStripLabel(group) { Font = Theme.Small });
            }
            var status = BackgroundServices.GetStatus(item, state);
            var mi = new ToolStripMenuItem(item.Title + (status == BgStatus.Active ? "" : $"   ({BackgroundServices.StatusText(status)})"))
            {
                Checked = status == BgStatus.Active,
                ToolTipText = status switch
                {
                    BgStatus.Active => "Работает. Нажмите, чтобы приостановить на время игры",
                    BgStatus.OffInWindows => "Отключено не через StaScan. Нажмите, чтобы включить (как в Windows по умолчанию)",
                    _ => "Нажмите, чтобы вернуть как было",
                },
            };
            var id = item.Id;
            mi.Click += async (_, _) => await RunToggle(id);
            items.Add(mi);
        }

        items.Add(new ToolStripSeparator());

        var permanent = new ToolStripMenuItem("Отключить насовсем");
        foreach (var item in available.Where(i => BackgroundServices.GetStatus(i, state) is BgStatus.Active or BgStatus.PausedForGame))
        {
            var it = item;
            permanent.DropDownItems.Add(it.Title, null, async (_, _) =>
            {
                if (MessageBox.Show($"«{it.Title}» будет отключено и не включится после перезагрузки, пока вы его не вернёте. Продолжить?",
                        "StaScan", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                var (_, errors) = await Task.Run(() => BackgroundServices.Apply(new[] { it.Id }, PauseMode.Permanent));
                Notify(errors.Count > 0 ? $"Не получилось: {errors[0]}" : $"{it.Title}: отключено насовсем",
                    errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
            });
        }
        permanent.Enabled = permanent.DropDownItems.Count > 0;
        items.Add(permanent);

        items.Add(new ToolStripMenuItem("Приостановить отмеченные на время игры", null, async (_, _) =>
        {
            var (ok, errors) = await Task.Run(() => BackgroundServices.Apply(_settings.BackgroundItemIds, PauseMode.Temporary));
            Notify($"Приостановлено: {ok}." + (errors.Count > 0 ? $" Не получилось: {string.Join("; ", errors)}" : ""),
                errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }));
        items.Add(new ToolStripMenuItem("Вернуть всё как было", null, async (_, _) => await RestoreBackground())
        {
            Enabled = state.Count > 0,
        });
        items.Add(new ToolStripMenuItem("Открыть список…", null, (_, _) => Open(Page.Services)));
    }

    async Task RunToggle(string id)
    {
        try
        {
            var text = await Task.Run(() => BackgroundServices.Toggle(id));
            Notify(text, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Write($"Трей: переключение {id} не удалось: {ex.Message}");
            Notify("Не получилось: " + ex.Message, ToolTipIcon.Warning);
        }
    }

    async Task RestoreBackground()
    {
        var (ok, errors) = await Task.Run(() => BackgroundServices.Restore());
        Notify($"Фоновые службы возвращены: {ok}." + (errors.Count > 0 ? $" Не получилось: {string.Join("; ", errors)}" : ""),
            errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    /// <summary>«На время игры» не должно переживать перезагрузку: возвращаем то, что приостанавливали в прошлом сеансе.</summary>
    async Task AutoRestoreAfterReboot()
    {
        try
        {
            var (ok, errors) = await Task.Run(() => BackgroundServices.Restore(onlyTemporaryFromOldBoot: true));
            if (ok > 0 || errors.Count > 0)
                Notify($"После перезагрузки вернул фоновые службы, приостановленные на время игры: {ok}." +
                       (errors.Count > 0 ? $" Ошибок: {errors.Count}, см. лог." : ""), errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Write($"Автооткат фоновых служб упал: {ex}");
        }
    }

    void RetakeBaseline()
    {
        _baseline = Baseline.Take();
        UpdateTooltip();
        Notify($"Новый снимок: {_baseline.Keys.Count} процессов считаются «чистыми».", ToolTipIcon.Info);
    }

    void UpdateTooltip() => _tray.Text = $"StaScan: снимок от {_baseline.TakenAt:HH:mm}";

    void Notify(string text, ToolTipIcon icon)
    {
        _tray.BalloonTipTitle = "StaScan";
        _tray.BalloonTipText = text.Length > 250 ? text[..247] + "…" : text;
        _tray.BalloonTipIcon = icon;
        _tray.ShowBalloonTip(8000);
    }

    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    /// <summary>
    /// В трее StaScan большую часть времени спит, поэтому после закрытия окна собираем мусор
    /// и отдаём рабочий набор системе: в простое процесс занимает считанные мегабайты физической памяти.
    /// </summary>
    static void TrimMemory()
    {
        Task.Run(() =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
        });
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _main?.Close();
        _affinity.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        Log.Write("StaScan закрыт");
        base.ExitThreadCore();
    }
}

static class TrayIcon
{
    /// <summary>Значок рисуем в коде, чтобы не таскать .ico: зелёный круг с треугольником «play».</summary>
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(34, 197, 94));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var fg = new SolidBrush(Color.White);
            g.FillPolygon(fg, new[] { new PointF(12, 8), new PointF(25, 16), new PointF(12, 24) });
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
