using System.Drawing.Drawing2D;
using System.Globalization;
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
    readonly GameWatcher _games;
    readonly MetricsHistory _metrics = new();
    readonly Hotkeys _hotkeys;
    readonly AppServices _app;
    readonly SynchronizationContext _ui;
    ToolStripMenuItem _gameModeItem = null!;
    ToolStripMenuItem _servicesMenu = null!;
    UpdateInfo? _pendingUpdate;
    Baseline _baseline;
    MainForm? _main;

    public TrayContext()
    {
        Theme.Set(_settings.Theme);
        L.Set(_settings.Language);
        (_baseline, bool reused) = Baseline.LoadOrTake();

        _menu = new ContextMenuStrip { Renderer = Theme.MenuRenderer() };
        _menu.Opening += (_, _) =>
        {
            _gameModeItem.Checked = _settings.GameMode;
            _gameModeItem.Text = _games?.ActiveGame is { } g ? L.F("Game mode (active: {0})", g) : L.T("Game mode");
        };
        BuildMenu();

        _tray = new NotifyIcon
        {
            Icon = TrayIcon.CreateSmall(),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => Open(Page.Prepare);
        UpdateTooltip();

        // notifications from the timer do not arrive on the UI thread
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _affinity = new AffinityWatcher(() => _settings.Cs2SkipCpu0, (t, i) => _ui.Post(_ => Notify(t, i), null));
        _games = new GameWatcher(_settings, () => _baseline, (t, i) => _ui.Post(_ => Notify(t, i), null));

        _hotkeys = new Hotkeys(OnHotkey);

        _app = new AppServices
        {
            Settings = _settings,
            GetBaseline = () => _baseline,
            RetakeBaseline = RetakeBaseline,
            SetBaselinePinned = SetBaselinePinned,
            Notify = Notify,
            Cs2SettingChanged = () => { _affinity.Update(); if (_settings.Cs2SkipCpu0) Task.Run(_affinity.Tick); },
            ChangeTheme = ChangeTheme,
            ChangeLanguage = ChangeLanguage,
            GameModeSettingsChanged = () => _games.Update(),
            GetActiveGame = () => (_games.ActiveGame, _games.ActiveSince),
            Metrics = _metrics,
            SetHotkeys = SetHotkeys,
            HotkeyFailures = () => _hotkeys.Failed.ToList(),
            PendingUpdate = () => _pendingUpdate,
            SetPendingUpdate = u => _pendingUpdate = u,
        };

        _games.StateChanged += _app.RaiseGameModeChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        if (_settings.Hotkeys) _hotkeys.Register();

        Autostart.RepairIfBroken();
        _ = AutoRestoreAfterReboot();

        if (!reused && Environment.TickCount64 > TimeSpan.FromMinutes(10).TotalMilliseconds)
        {
            Notify(L.T("The snapshot was taken now rather than at sign-in, so programs that are already open count as clean. Turn on \"Start with Windows\" in Settings."),
                ToolTipIcon.Info);
        }

        // Explorer registers the tray entry a moment after the icon appears, then we can promote it
        Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => TrayIcon.PromoteOnce(_settings));

        _tray.BalloonTipClicked += (_, _) => { if (_pendingUpdate != null) Open(Page.Settings); };
        _ = CheckUpdatesInBackground();

        // right after startup, give the memory used during initialization back to the system
        TrimMemory();
    }

    /// <summary>Builds the tray menu. Called again when the UI language changes.</summary>
    void BuildMenu()
    {
        _menu.Items.Clear();
        var prepare = new ToolStripMenuItem(L.T("Game prep…"), null, (_, _) => Open(Page.Prepare))
        {
            Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold),
        };
        _gameModeItem = new ToolStripMenuItem(L.T("Game mode"), null, (_, _) => ToggleGameMode());
        _servicesMenu = new ToolStripMenuItem(L.T("Background services"));
        _servicesMenu.DropDownItems.Add("…"); // placeholder so the submenu arrow is shown
        _servicesMenu.DropDownOpening += (_, _) => BuildServicesMenu();

        _menu.Items.Add(prepare);
        _menu.Items.Add(_gameModeItem);
        _menu.Items.Add(_servicesMenu);
        _menu.Items.Add(L.T("Diagnostics…"), null, (_, _) => Open(Page.Diagnostics));
        _menu.Items.Add(L.T("Mouse and keyboard USB ports…"), null, (_, _) => Open(Page.Usb));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(L.T("Open StaScan"), null, (_, _) => Open(Page.Prepare));
        _menu.Items.Add(L.T("Settings…"), null, (_, _) => Open(Page.Settings));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(L.T("Exit"), null, (_, _) => ExitThread());
    }

    /// <summary>Once a day, 30 seconds after start, look for a newer release on GitHub.</summary>
    async Task CheckUpdatesInBackground()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            if (!_settings.CheckUpdates || DateTime.Now - _settings.LastUpdateCheck < TimeSpan.FromHours(20)) return;
            var update = await Updater.CheckAsync();
            _settings.LastUpdateCheck = DateTime.Now;
            _settings.Save();
            if (update == null) return;
            _pendingUpdate = update;
            Log.Write($"Update available: {update.Tag}");
            Notify(L.F("StaScan {0} is available. Click here to update.", update.Version.ToString(3)), ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Write($"Update check failed: {ex.Message}");
        }
    }

    void OnHotkey(Hotkeys.Action action)
    {
        Log.Write($"Hotkey: {action}");
        switch (action)
        {
            case Hotkeys.Action.OpenPrep:
                Open(Page.Prepare);
                break;
            case Hotkeys.Action.ToggleGameMode:
                ToggleGameMode();
                break;
            case Hotkeys.Action.QuickBoost:
                Notify(L.T("Quick boost: freeing RAM…"), ToolTipIcon.Info);
                var options = new PrepOptions(new(), FreeMemory: true, RestartExplorer: false, DnsAndTemp: false,
                    BackgroundIds: _settings.PauseBackground ? _settings.BackgroundItemIds.ToList() : null);
                Task.Run(() =>
                {
                    var r = GamePrep.Run(options, _ => { });
                    _ui.Post(_ => Notify(L.T("Quick boost done.") + " " + r.Brief, r.Warn ? ToolTipIcon.Warning : ToolTipIcon.Info), null);
                });
                break;
        }
    }

    /// <summary>Turns the hotkeys on or off; returns the hotkeys that could not be registered.</summary>
    List<string> SetHotkeys(bool on)
    {
        _settings.Hotkeys = on;
        _settings.Save();
        if (on) _hotkeys.Register(); else _hotkeys.Unregister();
        return _hotkeys.Failed.ToList();
    }

    void ToggleGameMode()
    {
        _settings.GameMode = !_settings.GameMode;
        _settings.Save();
        _games.Update();
        _app.RaiseGameModeChanged();
        Log.Write($"Game mode {(_settings.GameMode ? "on" : "off")} (tray)");
        Notify(_settings.GameMode
            ? L.F("Game mode on. Waiting for: {0}.", string.Join(", ", _settings.Games))
            : L.T("Game mode off."), ToolTipIcon.Info);
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

    void ChangeLanguage(UiLanguage language, Page reopenAt)
    {
        _settings.Language = language;
        _settings.Save();
        L.Set(language);
        Log.Write($"UI language: {L.Code}");
        BuildMenu();
        UpdateTooltip();
        ReopenWindow(reopenAt);
    }

    void ApplyTheme(Page? reopenAt)
    {
        Theme.Set(_settings.Theme);
        _menu.Renderer = Theme.MenuRenderer();
        ReopenWindow(reopenAt);
    }

    /// <summary>Recreates the main window in place (after a theme or language change).</summary>
    void ReopenWindow(Page? reopenAt)
    {
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
                items.Add(new ToolStripLabel(L.T(group)) { Font = Theme.Small });
            }
            var status = BackgroundServices.GetStatus(item, state);
            var mi = new ToolStripMenuItem(L.T(item.Title) + (status == BgStatus.Active ? "" : $"   ({BackgroundServices.StatusText(status)})"))
            {
                Checked = status == BgStatus.Active,
                ToolTipText = status switch
                {
                    BgStatus.Active => L.T("Running. Click to pause for this gaming session"),
                    BgStatus.OffInWindows => L.T("Disabled outside StaScan. Click to enable it with the Windows default"),
                    _ => L.T("Click to restore"),
                },
            };
            var id = item.Id;
            mi.Click += async (_, _) => await RunToggle(id);
            items.Add(mi);
        }

        items.Add(new ToolStripSeparator());

        var permanent = new ToolStripMenuItem(L.T("Disable permanently"));
        foreach (var item in available.Where(i => BackgroundServices.GetStatus(i, state) is BgStatus.Active or BgStatus.PausedForGame))
        {
            var it = item;
            permanent.DropDownItems.Add(L.T(it.Title), null, async (_, _) =>
            {
                if (MessageBox.Show(L.F("\"{0}\" will be disabled and will stay off after a reboot until you restore it. Continue?", L.T(it.Title)),
                        "StaScan", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                var (_, errors) = await Task.Run(() => BackgroundServices.Apply(new[] { it.Id }, PauseMode.Permanent));
                Notify(errors.Count > 0 ? L.F("Failed: {0}", errors[0]) : L.F("{0}: disabled permanently", L.T(it.Title)),
                    errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
            });
        }
        permanent.Enabled = permanent.DropDownItems.Count > 0;
        items.Add(permanent);

        items.Add(new ToolStripMenuItem(L.T("Pause marked items for this session"), null, async (_, _) =>
        {
            var (ok, errors) = await Task.Run(() => BackgroundServices.Apply(_settings.BackgroundItemIds, PauseMode.Temporary));
            Notify(L.F("Paused: {0}.", ok) + (errors.Count > 0 ? " " + L.F("Failed: {0}", string.Join("; ", errors)) : ""),
                errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }));
        items.Add(new ToolStripMenuItem(L.T("Restore all"), null, async (_, _) => await RestoreBackground())
        {
            Enabled = state.Count > 0,
        });
        items.Add(new ToolStripMenuItem(L.T("Open list…"), null, (_, _) => Open(Page.Services)));
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
            Notify(L.F("Failed: {0}", ex.Message), ToolTipIcon.Warning);
        }
    }

    async Task RestoreBackground()
    {
        var (ok, errors) = await Task.Run(() => BackgroundServices.Restore());
        Notify(L.F("Background services restored: {0}.", ok) + (errors.Count > 0 ? " " + L.F("Failed: {0}", string.Join("; ", errors)) : ""),
            errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    /// <summary>"For this session" must not survive a reboot: restore whatever was paused in the previous session.</summary>
    async Task AutoRestoreAfterReboot()
    {
        try
        {
            var (ok, errors) = await Task.Run(() => BackgroundServices.Restore(onlyTemporaryFromOldBoot: true));
            if (ok > 0 || errors.Count > 0)
                Notify(L.F("Restored background services that were paused for the previous gaming session: {0}.", ok) +
                       (errors.Count > 0 ? " " + L.F("Errors: {0}, see the log.", errors.Count) : ""),
                    errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
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
        Notify(L.F("New snapshot: {0} processes now count as clean.", _baseline.Keys.Count), ToolTipIcon.Info);
    }

    void SetBaselinePinned(bool pinned)
    {
        _baseline.SetPinned(pinned);
        UpdateTooltip();
    }

    void UpdateTooltip()
    {
        var text = _baseline.Pinned
            ? L.F("StaScan: pinned snapshot from {0}", L.Day(_baseline.TakenAt))
            : L.F("StaScan: snapshot from {0}", _baseline.TakenAt.ToString("HH:mm", CultureInfo.InvariantCulture));
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

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
        _games.Dispose();
        _metrics.Dispose();
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        Log.Write("StaScan exited");
        base.ExitThreadCore();
    }
}

static class TrayIcon
{
    /// <summary>The app icon (assets/StaScan.ico, embedded in the assembly) at the requested size.</summary>
    public static Icon Create(Size? size = null)
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("StaScan.ico")!;
        return size is { } s ? new Icon(stream, s) : new Icon(stream);
    }

    /// <summary>Icon for the notification area at the current DPI.</summary>
    public static Icon CreateSmall() => Create(SystemInformation.SmallIconSize);

    /// <summary>
    /// Windows 11 hides new tray icons in the overflow menu. The first time StaScan runs from a given location it
    /// marks its own entry in "Control Panel\NotifyIconSettings" as promoted, so the icon is always visible on the
    /// taskbar. This is done only once: if the user hides the icon later, that choice is respected.
    /// </summary>
    public static void PromoteOnce(Settings settings)
    {
        var exe = Environment.ProcessPath;
        if (exe == null || Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return;
        if (settings.TrayPromotedFor.Contains(exe, StringComparer.OrdinalIgnoreCase)) return;

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return; // Windows 10 or older Windows 11 builds: nothing to do
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name, writable: true);
                if (key?.GetValue("ExecutablePath") is not string path ||
                    !string.Equals(ExpandKnownFolder(path), exe, StringComparison.OrdinalIgnoreCase)) continue;

                if (key.GetValue("IsPromoted") is not int promoted || promoted == 0)
                    key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                settings.TrayPromotedFor.Add(exe);
                settings.Save();
                Log.Write("Tray icon set to always visible");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not make the tray icon always visible: {ex.Message}");
        }
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>
    /// Explorer stores paths under known folders as "{FOLDERID}\rest", for example
    /// "{6D809377-6AF0-444B-8957-A3773F02200E}\StaScan\StaScan.exe" for Program Files.
    /// </summary>
    static string ExpandKnownFolder(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(path, @"^\{([0-9A-Fa-f-]{36})\}\\(.*)$");
        if (!m.Success) return path;
        if (SHGetKnownFolderPath(Guid.Parse(m.Groups[1].Value), 0, IntPtr.Zero, out var ptr) != 0) return path;
        try
        {
            return Path.Combine(Marshal.PtrToStringUni(ptr)!, m.Groups[2].Value);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }
}
