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
    readonly Label _state, _saved;
    bool _loading;

    public bool IsBusy => false;
    Settings S => _app.Settings;

    public GameModePage(AppServices app)
    {
        _app = app;

        var title = Theme.PageTitle("Game mode");
        var hint = Theme.PageHint(
            "When a game from the list starts, StaScan prepares the PC automatically. When the last game exits, " +
            "the background services it paused are restored.", 40);

        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(20, 0, 20, 16),
        };

        _enabled = Theme.Check("Turn on game mode automatically when a game starts", S.GameMode);
        _enabled.Font = Theme.Bold;
        _enabled.CheckedChanged += (_, _) => Save();
        _state = new Label { AutoSize = true, ForeColor = Theme.P.Muted, Margin = new Padding(20, 2, 0, 0) };
        body.Controls.Add(_enabled);
        body.Controls.Add(_state);

        body.Controls.Add(Theme.Section("When a game starts"));
        _freeMemory = Theme.Check("Free up RAM", S.GameModeFreeMemory);
        _pauseServices = Theme.Check("Pause background services marked \"Include in prep\"", S.GameModePauseServices);
        _closeApps = Theme.Check("Close programs started since the snapshot (no confirmation, the game itself is kept)", S.GameModeCloseApps);
        _restore = Theme.Check("Restore paused services when the last game exits", S.GameModeRestoreOnExit);
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
            Text = "CS2 CPU 0 exclusion is set in the Game prep section and works independently of game mode.",
        });

        body.Controls.Add(Theme.Section("Games"));
        body.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Theme.P.Muted,
            Text = "One executable name per line, for example cs2.exe",
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
        var add = Theme.Secondary("Add running program");
        add.Click += (_, _) => AddRunning();
        var refresh = Theme.Secondary("Refresh");
        refresh.Click += (_, _) => FillRunning();
        var addLine = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        addLine.Controls.AddRange(new Control[] { _running, add, refresh });
        body.Controls.Add(addLine);

        var save = Theme.Primary("Save game list");
        save.Margin = new Padding(0, 10, 0, 0);
        _saved = new Label { AutoSize = true, ForeColor = Theme.P.Accent, Margin = new Padding(12, 19, 0, 0) };
        save.Click += (_, _) => SaveGames();
        var saveLine = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        saveLine.Controls.AddRange(new Control[] { save, _saved });
        body.Controls.Add(saveLine);

        Controls.Add(body);
        Controls.Add(hint);
        Controls.Add(title);

        _app.GameModeChanged += OnStateChanged;
        Disposed += (_, _) => _app.GameModeChanged -= OnStateChanged;
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
            ? "Game mode is off."
            : game != null
                ? $"Active: {game} since {since?.ToString("HH:mm", CultureInfo.InvariantCulture)}."
                : "Waiting for a game to start.";
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
        _saved.Text = "Saved";
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
