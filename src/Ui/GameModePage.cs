using System.Diagnostics;
using System.Globalization;

namespace StaScan;

/// <summary>"Game mode" section: automatic game prep when a game from the list starts.</summary>
sealed class GameModePage : UserControl, IPage
{
    readonly AppServices _app;
    readonly CheckBox _enabled, _freeMemory, _pauseServices, _closeApps, _restore;
    readonly TextBox _games;
    readonly ComboBox _running;
    readonly Label _state, _saved, _fpsStatus;
    readonly ComboBox _fpsTarget;
    readonly ListView _fps;
    readonly UiButton _measure, _install;
    bool _measuring;
    bool _loading;

    public bool IsBusy => false;
    Settings S => _app.Settings;

    public GameModePage(AppServices app)
    {
        _app = app;

        var title = Theme.PageTitle(L.T("Game mode"));
        var hint = Theme.PageHint(
            L.T("When a game from the list starts, StaScan prepares the PC automatically. When the last game exits, the background services it paused are restored."), 40);

        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(20, 0, 20, 16),
        };

        _enabled = Theme.Check(L.T("Turn on game mode automatically when a game starts"), S.GameMode);
        _enabled.Font = Theme.Bold;
        _enabled.CheckedChanged += (_, _) => Save();
        _state = new Label { AutoSize = true, ForeColor = Theme.P.Muted, Margin = new Padding(20, 2, 0, 0) };
        body.Controls.Add(_enabled);
        body.Controls.Add(_state);

        body.Controls.Add(Theme.Section(L.T("When a game starts")));
        _freeMemory = Theme.Check(L.T("Free up RAM"), S.GameModeFreeMemory);
        _pauseServices = Theme.Check(L.T("Pause background services marked \"Include in prep\""), S.GameModePauseServices);
        _closeApps = Theme.Check(L.T("Close programs started since the snapshot (no confirmation, the game itself is kept)"), S.GameModeCloseApps);
        _restore = Theme.Check(L.T("Restore paused services when the last game exits"), S.GameModeRestoreOnExit);
        foreach (var c in new[] { _freeMemory, _pauseServices, _closeApps, _restore })
        {
            c.CheckedChanged += (_, _) => Save();
            body.Controls.Add(c);
        }
        body.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Theme.P.Muted,
            Margin = new Padding(0, 4, 0, 0),
            Text = L.T("CS2 CPU 0 exclusion is set in the Game prep section and works independently of game mode."),
        });

        body.Controls.Add(Theme.Section(L.T("Games")));
        body.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Theme.P.Muted,
            Text = L.T("One executable name per line, for example cs2.exe"),
        });
        _games = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.Mono,
            Width = 440,
            Height = 140,
            Margin = new Padding(0, 6, 0, 8),
        };
        body.Controls.Add(_games);

        _running = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Margin = new Padding(0, 4, 8, 0) };
        var add = Theme.Secondary(L.T("Add running program"));
        add.Click += (_, _) => AddRunning();
        var refresh = Theme.Secondary(L.T("Refresh"));
        refresh.Click += (_, _) => FillRunning();
        var addLine = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        addLine.Controls.AddRange(new Control[] { _running, add, refresh });
        body.Controls.Add(addLine);

        var save = Theme.Primary(L.T("Save game list"));
        save.Margin = new Padding(0, 10, 0, 0);
        _saved = new Label { AutoSize = true, ForeColor = Theme.P.Accent, Margin = new Padding(12, 19, 0, 0) };
        save.Click += (_, _) => SaveGames();
        var saveLine = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        saveLine.Controls.AddRange(new Control[] { save, _saved });
        body.Controls.Add(saveLine);

        // FPS measurement
        body.Controls.Add(Theme.Section(L.T("FPS measurement")));
        body.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            ForeColor = Theme.P.Muted,
            Text = L.T("Press Ctrl+Alt+F while playing to measure the game for 20 seconds. Measure once before game prep and once after it to see whether it helps. Measuring from this window works too, but games often slow down when they are not in the foreground."),
        });
        _fpsTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Margin = new Padding(0, 8, 8, 0) };
        _measure = Theme.Primary(L.F("Measure for {0} s", FpsMeter.DefaultSeconds));
        _measure.Margin = new Padding(0, 6, 8, 0);
        _measure.Click += async (_, _) => await MeasureAsync();
        _install = Theme.Secondary(L.T("Download PresentMon"));
        _install.Margin = new Padding(0, 6, 8, 0);
        _install.Click += async (_, _) => await InstallPresentMonAsync();
        var fpsLine = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        fpsLine.Controls.AddRange(new Control[] { _fpsTarget, _measure, _install });
        body.Controls.Add(fpsLine);
        _fpsStatus = new Label { AutoSize = true, ForeColor = Theme.P.Muted, Margin = new Padding(0, 6, 0, 6) };
        body.Controls.Add(_fpsStatus);
        _fps = Theme.List();
        _fps.Dock = DockStyle.None;
        _fps.Size = new Size(760, 170);
        _fps.Columns.Add(L.T("Time"), 130);
        _fps.Columns.Add(L.T("Program"), 170);
        _fps.Columns.Add(L.T("Average FPS"), 100, HorizontalAlignment.Right);
        _fps.Columns.Add(L.T("1% low"), 90, HorizontalAlignment.Right);
        _fps.Columns.Add(L.T("Change"), 90, HorizontalAlignment.Right);
        _fps.Columns.Add(L.T("After prep"), 100);
        body.Controls.Add(_fps);

        Controls.Add(body);
        Controls.Add(hint);
        Controls.Add(title);

        _app.GameModeChanged += OnStateChanged;
        _app.FpsMeasured += OnFpsMeasured;
        Disposed += (_, _) =>
        {
            _app.GameModeChanged -= OnStateChanged;
            _app.FpsMeasured -= OnFpsMeasured;
        };
    }

    void OnFpsMeasured()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(FillFps);
    }

    void FillFps()
    {
        _install.Visible = !FpsMeter.IsInstalled;
        _measure.Enabled = FpsMeter.IsInstalled && !_measuring;
        if (!FpsMeter.IsInstalled)
            _fpsStatus.Text = L.T("FPS is measured with Intel PresentMon (free, MIT licence, about 1 MB). It is downloaded from GitHub once and verified by checksum.");

        var all = FpsMeter.Load();
        _fps.BeginUpdate();
        _fps.Items.Clear();
        for (int i = all.Count - 1; i >= 0; i--)
        {
            var r = all[i];
            var prev = all.Take(i).LastOrDefault(x => x.Process.Equals(r.Process, StringComparison.OrdinalIgnoreCase));
            _fps.Items.Add(new ListViewItem(new[]
            {
                L.DayTime(r.At),
                r.Process,
                r.AvgFps.ToString("0", CultureInfo.InvariantCulture),
                r.Low1Fps.ToString("0", CultureInfo.InvariantCulture),
                prev != null ? FpsMeter.Change(prev.AvgFps, r.AvgFps) : "",
                r.AfterPrep ? L.T("yes") : "",
            }));
        }
        _fps.EndUpdate();
    }

    async Task InstallPresentMonAsync()
    {
        if (MessageBox.Show(FindForm(), L.T("Download Intel PresentMon 2.6.0 (about 1 MB) from github.com/GameTechDev/PresentMon? It is verified by checksum before use."),
                "StaScan", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _install.Enabled = false;
        _fpsStatus.Text = L.T("Downloading PresentMon…");
        try
        {
            await FpsMeter.InstallAsync();
            _fpsStatus.Text = L.T("PresentMon is ready. Press Ctrl+Alt+F in a game, or pick a program and click Measure.");
        }
        catch (Exception ex)
        {
            _fpsStatus.ForeColor = Theme.P.Warn;
            _fpsStatus.Text = L.F("Could not download PresentMon: {0}", ex.Message);
            Log.Write($"PresentMon download failed: {ex}");
        }
        finally
        {
            _install.Enabled = true;
            FillFps();
        }
    }

    async Task MeasureAsync()
    {
        if (_fpsTarget.SelectedItem is not string process) return;
        _measuring = true;
        _measure.Enabled = false;
        _fpsStatus.ForeColor = Theme.P.Muted;
        _fpsStatus.Text = L.F("Measuring FPS of {0} for {1} seconds…", process, FpsMeter.DefaultSeconds);
        try
        {
            var result = await FpsMeter.MeasureAsync(process);
            _fpsStatus.Text = FpsMeter.Describe(result);
        }
        catch (Exception ex)
        {
            _fpsStatus.ForeColor = Theme.P.Warn;
            _fpsStatus.Text = L.F("FPS measurement failed: {0}", ex.Message);
        }
        finally
        {
            _measuring = false;
            FillFps();
        }
    }

    public void OnShown()
    {
        _loading = true;
        _enabled.Checked = S.GameMode;
        _games.Text = string.Join(Environment.NewLine, S.Games);
        _saved.Text = "";
        _loading = false;
        FillRunning();
        UpdateState();
        FillFps();
    }

    void OnStateChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(UpdateState);
    }

    void UpdateState()
    {
        var (game, since) = _app.GetActiveGame();
        _state.Text = !S.GameMode
            ? L.T("Game mode is off.")
            : game != null
                ? L.F("Active: {0} since {1}.", game, since?.ToString("HH:mm", CultureInfo.InvariantCulture))
                : L.T("Waiting for a game to start.");
        _state.ForeColor = game != null ? Theme.P.Accent : Theme.P.Muted;
    }

    /// <summary>Programs with a visible window, so the list is short and recognisable.</summary>
    void FillRunning()
    {
        _running.Items.Clear();
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && p.SessionId == Process.GetCurrentProcess().SessionId)
                        names.Add(p.ProcessName + ".exe");
                }
                catch { }
            }
        }
        foreach (var n in names) _running.Items.Add(n);

        // FPS targets: games from the list first, then programs with a window
        var selected = _fpsTarget.SelectedItem as string;
        _fpsTarget.Items.Clear();
        foreach (var n in S.Games.Concat(names).Distinct(StringComparer.OrdinalIgnoreCase)) _fpsTarget.Items.Add(n);
        if (_fpsTarget.Items.Count > 0) _fpsTarget.SelectedItem = selected != null && _fpsTarget.Items.Contains(selected) ? selected : _fpsTarget.Items[0];
        if (_running.Items.Count > 0) _running.SelectedIndex = 0;
    }

    void AddRunning()
    {
        if (_running.SelectedItem is not string exe) return;
        var lines = _games.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (!lines.Contains(exe, StringComparer.OrdinalIgnoreCase)) lines.Add(exe);
        _games.Text = string.Join(Environment.NewLine, lines);
        SaveGames();
    }

    void SaveGames()
    {
        S.Games = _games.Lines.Select(l => l.Trim()).Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Save();
        _saved.Text = L.T("Saved");
        Log.Write($"Game list: {string.Join(", ", S.Games)}");
    }

    void Save()
    {
        if (_loading) return;
        bool wasOn = S.GameMode;
        S.GameMode = _enabled.Checked;
        S.GameModeFreeMemory = _freeMemory.Checked;
        S.GameModePauseServices = _pauseServices.Checked;
        S.GameModeCloseApps = _closeApps.Checked;
        S.GameModeRestoreOnExit = _restore.Checked;
        S.Save();
        if (wasOn != S.GameMode) Log.Write($"Game mode {(S.GameMode ? "on" : "off")}");
        _app.GameModeSettingsChanged();
        UpdateState();
    }
}
