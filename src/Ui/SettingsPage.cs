using System.Diagnostics;

namespace StaScan;

/// <summary>"Settings" section: theme, snapshot, autostart, whitelist, log.</summary>
sealed class SettingsPage : UserControl, IPage
{
    readonly AppServices _app;
    readonly Label _snapshot;
    readonly CheckBox _autostart;
    readonly TextBox _whitelist;
    readonly Label _saved;
    readonly RadioButton _themeSystem, _themeLight, _themeDark;
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

        // Snapshot
        body.Controls.Add(Theme.Section("Clean-state snapshot"));
        _snapshot = new Label { AutoSize = true, ForeColor = Theme.P.Muted, Margin = new Padding(0, 0, 0, 8) };
        body.Controls.Add(_snapshot);
        var retake = Theme.Secondary("Retake snapshot (treat the current state as clean)");
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
                         "Anything started later is offered for closing during game prep.";
        _autostart.Checked = Autostart.IsEnabled;
        _whitelist.Text = string.Join(Environment.NewLine, _app.Settings.Whitelist);
        _saved.Text = "";
        _themeSystem.Checked = _app.Settings.Theme == ThemeMode.System;
        _themeLight.Checked = _app.Settings.Theme == ThemeMode.Light;
        _themeDark.Checked = _app.Settings.Theme == ThemeMode.Dark;
        _loading = false;
    }

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
