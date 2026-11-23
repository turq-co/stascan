using System.Diagnostics;

namespace StaScan;

/// <summary>"Settings" section: theme, snapshot, autostart, whitelist, log.</summary>
sealed class SettingsPage : UserControl, IPage
{
    readonly AppServices _app;
    readonly Label _snapshot;
    readonly CheckBox _autostart, _pinned, _hotkeys;
    readonly Label _hotkeyNote;
    readonly TextBox _whitelist;
    readonly Label _saved;
    readonly RadioButton _themeSystem, _themeLight, _themeDark;
    readonly Label _updateStatus;
    readonly CheckBox _autoUpdate;
    readonly UiButton _checkNow, _install;
    bool _loading;

    public bool IsBusy => false;

    public SettingsPage(AppServices app)
    {
        _app = app;

        var title = Theme.PageTitle("Settings");
        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(20, 0, 20, 16),
        };

        // Theme
        body.Controls.Add(Theme.Section("Appearance"));
        _themeSystem = Radio("Use Windows setting");
        _themeLight = Radio("Light");
        _themeDark = Radio("Dark");
        var themeLine = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        themeLine.Controls.AddRange(new Control[] { _themeSystem, _themeLight, _themeDark });
        body.Controls.Add(themeLine);

        // Updates
        body.Controls.Add(Theme.Section("Updates"));
        _updateStatus = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        body.Controls.Add(_updateStatus);
        _autoUpdate = Theme.Check("Check for updates automatically (once a day, from GitHub Releases)", false);
        _autoUpdate.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _app.Settings.CheckUpdates = _autoUpdate.Checked;
            _app.Settings.Save();
        };
        body.Controls.Add(_autoUpdate);
        _checkNow = Theme.Secondary("Check now");
        _checkNow.Click += async (_, _) => await CheckNowAsync();
        _install = Theme.Primary("Download and install");
        _install.Click += async (_, _) => await InstallAsync();
        var notes = Theme.Secondary("Release notes");
        notes.Click += (_, _) => OpenUrl(_app.PendingUpdate()?.PageUrl ?? Updater.ReleasesPage);
        var updateLine = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
        updateLine.Controls.AddRange(new Control[] { _checkNow, _install, notes });
        foreach (Control c in updateLine.Controls) c.Margin = new Padding(0, 0, 8, 0);
        body.Controls.Add(updateLine);

        // Snapshot
        body.Controls.Add(Theme.Section("Clean-state snapshot"));
        _snapshot = new Label { AutoSize = true, ForeColor = Theme.P.Muted, Margin = new Padding(0, 0, 0, 8) };
        body.Controls.Add(_snapshot);
        _pinned = Theme.Check("Pin this snapshot (keep it across reboots instead of taking a new one at sign-in)", false);
        _pinned.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _app.SetBaselinePinned(_pinned.Checked);
            OnShown();
        };
        body.Controls.Add(_pinned);
        var retake = Theme.Secondary("Retake snapshot (treat the current state as clean)");
        retake.Margin = new Padding(0, 8, 0, 0);
        retake.Click += (_, _) => { _app.RetakeBaseline(); OnShown(); };
        body.Controls.Add(retake);

        // Autostart
        body.Controls.Add(Theme.Section("Startup"));
        _autostart = Theme.Check("Start with Windows (no UAC prompt, via Task Scheduler)", false);
        _autostart.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            bool ok = _autostart.Checked ? Autostart.Enable() : Autostart.Disable();
            if (!ok)
            {
                _app.Notify("Could not change autostart, see the log for details.", ToolTipIcon.Warning);
                _loading = true;
                _autostart.Checked = Autostart.IsEnabled;
                _loading = false;
            }
        };
        body.Controls.Add(_autostart);

        // Hotkeys
        body.Controls.Add(Theme.Section("Hotkeys"));
        _hotkeys = Theme.Check("Enable global hotkeys", false);
        _hotkeys.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            var failed = _app.SetHotkeys(_hotkeys.Checked);
            UpdateHotkeyNote(failed);
        };
        body.Controls.Add(_hotkeys);
        body.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Theme.P.Muted,
            Margin = new Padding(20, 0, 0, 0),
            Text = string.Join("\n", Hotkeys.All.Select(h => $"{h.keys}   {h.description}")),
        });
        _hotkeyNote = new Label { AutoSize = true, ForeColor = Theme.P.Warn, Margin = new Padding(20, 4, 0, 0) };
        body.Controls.Add(_hotkeyNote);

        // Whitelist
        body.Controls.Add(Theme.Section("Whitelist"));
        body.Controls.Add(new Label
        {
            Text = "These programs are never offered for closing. One executable name per line, for example steam.exe",
            AutoSize = true,
            ForeColor = Theme.P.Muted,
        });
        _whitelist = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.Mono,
            Width = 440,
            Height = 200,
            Margin = new Padding(0, 6, 0, 8),
        };
        body.Controls.Add(_whitelist);
        var save = Theme.Primary("Save whitelist");
        _saved = new Label { AutoSize = true, ForeColor = Theme.P.Accent, Margin = new Padding(12, 9, 0, 0) };
        save.Click += (_, _) => SaveWhitelist();
        var saveLine = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        saveLine.Controls.AddRange(new Control[] { save, _saved });
        body.Controls.Add(saveLine);

        // Log
        body.Controls.Add(Theme.Section("Log and data"));
        var openLog = Theme.Secondary("Open log folder");
        openLog.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Dir}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write($"Could not open the folder: {ex.Message}"); }
        };
        body.Controls.Add(openLog);

        Controls.Add(body);
        Controls.Add(title);
    }

    RadioButton Radio(string text)
    {
        var rb = new RadioButton { Text = text, AutoSize = true, Margin = new Padding(0, 2, 18, 2) };
        rb.CheckedChanged += (_, _) =>
        {
            if (_loading || !rb.Checked) return;
            var mode = rb == _themeDark ? ThemeMode.Dark : rb == _themeLight ? ThemeMode.Light : ThemeMode.System;
            if (mode == _app.Settings.Theme) return;
            // recreate the window after the click handler has returned
            BeginInvoke(() => _app.ChangeTheme(mode, Page.Settings));
        };
        return rb;
    }

    public void OnShown()
    {
        _loading = true;
        var b = _app.GetBaseline();
        _snapshot.Text = $"Taken {b.TakenAt.ToString("MMM d, yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)}, {b.Keys.Count} processes. " +
                         "Anything started later is offered for closing during game prep.\n" +
                         (b.Pinned
                             ? "Pinned: this snapshot is kept across reboots until you unpin or retake it."
                             : "Not pinned: a new snapshot is taken after every reboot, one minute after you sign in.");
        _pinned.Checked = b.Pinned;
        _autostart.Checked = Autostart.IsEnabled;
        _hotkeys.Checked = _app.Settings.Hotkeys;
        UpdateHotkeyNote(_app.Settings.Hotkeys ? _app.HotkeyFailures() : new());
        _autoUpdate.Checked = _app.Settings.CheckUpdates;
        ShowUpdateState();
        _whitelist.Text = string.Join(Environment.NewLine, _app.Settings.Whitelist);
        _saved.Text = "";
        _themeSystem.Checked = _app.Settings.Theme == ThemeMode.System;
        _themeLight.Checked = _app.Settings.Theme == ThemeMode.Light;
        _themeDark.Checked = _app.Settings.Theme == ThemeMode.Dark;
        _loading = false;
    }

    void ShowUpdateState(string? message = null)
    {
        var pending = _app.PendingUpdate();
        _install.Visible = pending?.SetupUrl != null;
        _updateStatus.ForeColor = pending != null ? Theme.P.Accent : Theme.P.Muted;
        _updateStatus.Text = message ?? (pending != null
            ? $"StaScan {pending.Version.ToString(3)} is available (you have {Updater.Current.ToString(3)})."
            : $"You have StaScan {Updater.Current.ToString(3)}." +
              (_app.Settings.LastUpdateCheck > DateTime.MinValue
                  ? $" Last checked {_app.Settings.LastUpdateCheck.ToString("MMM d, HH:mm", System.Globalization.CultureInfo.InvariantCulture)}."
                  : ""));
    }

    async Task CheckNowAsync()
    {
        _checkNow.Enabled = false;
        ShowUpdateState("Checking GitHub…");
        try
        {
            var update = await Updater.CheckAsync();
            _app.Settings.LastUpdateCheck = DateTime.Now;
            _app.Settings.Save();
            _app.SetPendingUpdate(update);
            ShowUpdateState(update == null ? $"You have the latest version ({Updater.Current.ToString(3)})." : null);
        }
        catch (Exception ex)
        {
            _updateStatus.ForeColor = Theme.P.Warn;
            _updateStatus.Text = "Could not check for updates: " + ex.Message;
        }
        finally
        {
            _checkNow.Enabled = true;
        }
    }

    async Task InstallAsync()
    {
        if (_app.PendingUpdate() is not { } update) return;
        _install.Enabled = _checkNow.Enabled = false;
        try
        {
            var progress = new Progress<int>(p => _updateStatus.Text = $"Downloading StaScan {update.Version.ToString(3)}… {p}%");
            var setup = await Updater.DownloadAsync(update, progress);
            _updateStatus.Text = "Installing. StaScan will restart in a moment…";
            Updater.Install(setup);
        }
        catch (Exception ex)
        {
            _updateStatus.ForeColor = Theme.P.Warn;
            _updateStatus.Text = "Update failed: " + ex.Message;
            Log.Write($"Update failed: {ex}");
            _install.Enabled = _checkNow.Enabled = true;
        }
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Could not open {url}: {ex.Message}"); }
    }

    void UpdateHotkeyNote(List<string> failed) =>
        _hotkeyNote.Text = failed.Count == 0 ? "" : $"! Already used by another program: {string.Join(", ", failed)}";

    void SaveWhitelist()
    {
        _app.Settings.Whitelist = _whitelist.Lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _app.Settings.Save();
        Log.Write($"Whitelist updated: {_app.Settings.Whitelist.Count} entries");
        _saved.Text = "Saved";
    }
}
