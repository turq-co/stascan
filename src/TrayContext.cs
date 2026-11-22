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
        var prepare = new ToolStripMenuItem("Game prep…", null, (_, _) => Open(Page.Prepare))
        {
            Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold),
        };
        _servicesMenu = new ToolStripMenuItem("Background services");
        _servicesMenu.DropDownItems.Add("…"); // placeholder so the submenu arrow is shown
        _servicesMenu.DropDownOpening += (_, _) => BuildServicesMenu();

        _menu.Items.Add(prepare);
        _menu.Items.Add(_servicesMenu);
        _menu.Items.Add("Mouse and keyboard USB ports…", null, (_, _) => Open(Page.Usb));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Open StaScan", null, (_, _) => Open(Page.Prepare));
        _menu.Items.Add("Settings…", null, (_, _) => Open(Page.Settings));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = TrayIcon.Create(),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => Open(Page.Prepare);
        UpdateTooltip();

        // notifications from the timer do not arrive on the UI thread
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _affinity = new AffinityWatcher(() => _settings.Cs2SkipCpu0, (t, i) => _ui.Post(_ => Notify(t, i), null));

        _app = new AppServices
        {
            Settings = _settings,
            GetBaseline = () => _baseline,
            RetakeBaseline = RetakeBaseline,
            SetBaselinePinned = SetBaselinePinned,
            Notify = Notify,
            Cs2SettingChanged = () => { _affinity.Update(); if (_settings.Cs2SkipCpu0) Task.Run(_affinity.Tick); },
            ChangeTheme = ChangeTheme,
        };

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        Autostart.RefreshIfEnabled();
        _ = AutoRestoreAfterReboot();

        if (!reused && Environment.TickCount64 > TimeSpan.FromMinutes(10).TotalMilliseconds)
        {
            Notify("The snapshot was taken now rather than at sign-in, so programs that are already open count as clean. " +
                   "Turn on \"Start with Windows\" in Settings.", ToolTipIcon.Info);
        }

        // right after startup, give the memory used during initialization back to the system
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

    /// <summary>"Use Windows setting" theme: follow light/dark switches in Windows Settings.</summary>
    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || _settings.Theme != ThemeMode.System) return;
        if (Theme.SystemIsDark() == Theme.P.IsDark) return;
        _ui.Post(_ => ApplyTheme(Page.Settings), null);
    }

    /// <summary>"Background services" submenu: clicking a service toggles it (running ⇄ paused for the session).</summary>
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
                    BgStatus.Active => "Running. Click to pause for this gaming session",
                    BgStatus.OffInWindows => "Disabled outside StaScan. Click to enable it with the Windows default",
                    _ => "Click to restore",
                },
            };
            var id = item.Id;
            mi.Click += async (_, _) => await RunToggle(id);
            items.Add(mi);
        }

        items.Add(new ToolStripSeparator());

        var permanent = new ToolStripMenuItem("Disable permanently");
        foreach (var item in available.Where(i => BackgroundServices.GetStatus(i, state) is BgStatus.Active or BgStatus.PausedForGame))
        {
            var it = item;
            permanent.DropDownItems.Add(it.Title, null, async (_, _) =>
            {
                if (MessageBox.Show($"\"{it.Title}\" will be disabled and will stay off after a reboot until you restore it. Continue?",
                        "StaScan", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                var (_, errors) = await Task.Run(() => BackgroundServices.Apply(new[] { it.Id }, PauseMode.Permanent));
                Notify(errors.Count > 0 ? $"Failed: {errors[0]}" : $"{it.Title}: disabled permanently",
                    errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
            });
        }
        permanent.Enabled = permanent.DropDownItems.Count > 0;
        items.Add(permanent);

        items.Add(new ToolStripMenuItem("Pause marked items for this session", null, async (_, _) =>
        {
            var (ok, errors) = await Task.Run(() => BackgroundServices.Apply(_settings.BackgroundItemIds, PauseMode.Temporary));
            Notify($"Paused: {ok}." + (errors.Count > 0 ? $" Failed: {string.Join("; ", errors)}" : ""),
                errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }));
        items.Add(new ToolStripMenuItem("Restore all", null, async (_, _) => await RestoreBackground())
        {
            Enabled = state.Count > 0,
        });
        items.Add(new ToolStripMenuItem("Open list…", null, (_, _) => Open(Page.Services)));
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
            Log.Write($"Tray: toggling {id} failed: {ex.Message}");
            Notify("Failed: " + ex.Message, ToolTipIcon.Warning);
        }
    }

    async Task RestoreBackground()
    {
        var (ok, errors) = await Task.Run(() => BackgroundServices.Restore());
        Notify($"Background services restored: {ok}." + (errors.Count > 0 ? $" Failed: {string.Join("; ", errors)}" : ""),
            errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    /// <summary>"For this session" must not survive a reboot: restore whatever was paused in the previous session.</summary>
    async Task AutoRestoreAfterReboot()
    {
        try
        {
            var (ok, errors) = await Task.Run(() => BackgroundServices.Restore(onlyTemporaryFromOldBoot: true));
            if (ok > 0 || errors.Count > 0)
                Notify($"Restored background services that were paused for the previous gaming session: {ok}." +
                       (errors.Count > 0 ? $" Errors: {errors.Count}, see the log." : ""), errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Write($"Automatic restore of background services failed: {ex}");
        }
    }

    void RetakeBaseline()
    {
        // a retaken snapshot keeps the pinned state, so a pinned snapshot stays pinned
        _baseline = Baseline.Take(pinned: _baseline.Pinned);
        UpdateTooltip();
        Notify($"New snapshot: {_baseline.Keys.Count} processes now count as clean.", ToolTipIcon.Info);
    }

    void SetBaselinePinned(bool pinned)
    {
        _baseline.SetPinned(pinned);
        UpdateTooltip();
    }

    void UpdateTooltip() => _tray.Text = _baseline.Pinned
        ? $"StaScan: pinned snapshot from {_baseline.TakenAt.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture)}"
        : $"StaScan: snapshot from {_baseline.TakenAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)}";

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
    /// StaScan sleeps in the tray most of the time, so after the window is closed we collect garbage
    /// and hand the working set back to the system: when idle the process uses only a few MB of physical memory.
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
        Log.Write("StaScan exited");
        base.ExitThreadCore();
    }
}

static class TrayIcon
{
    /// <summary>The icon is drawn in code so no .ico file is needed: a green circle with a play triangle.</summary>
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
