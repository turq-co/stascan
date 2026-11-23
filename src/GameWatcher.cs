using System.Diagnostics;

namespace StaScan;

/// <summary>
/// Game mode: watches for games from the list and runs game prep automatically when one starts,
/// then restores the background services it paused after the last game exits.
/// Checks every 4 seconds only while game mode is on; otherwise the timer is stopped.
/// </summary>
sealed class GameWatcher : IDisposable
{
    readonly Settings _settings;
    readonly Func<Baseline> _baseline;
    readonly Action<string, ToolTipIcon> _notify;
    readonly System.Threading.Timer _timer;
    readonly List<string> _pausedByGameMode = new();
    int _busy;

    /// <summary>The game that activated game mode, or null when no game is running.</summary>
    public string? ActiveGame { get; private set; }
    public DateTime? ActiveSince { get; private set; }

    /// <summary>Raised (not on the UI thread) when a game starts or stops.</summary>
    public event Action? StateChanged;

    public GameWatcher(Settings settings, Func<Baseline> baseline, Action<string, ToolTipIcon> notify)
    {
        _settings = settings;
        _baseline = baseline;
        _notify = notify;
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        Update();
    }

    public void Update()
    {
        if (_settings.GameMode) _timer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        else
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            if (ActiveGame != null) Task.Run(() => Deactivate("game mode turned off"));
        }
    }

    string? FindRunningGame()
    {
        foreach (var game in _settings.Games)
        {
            var name = Path.GetFileNameWithoutExtension(game.Trim());
            if (name.Length == 0) continue;
            var procs = Process.GetProcessesByName(name);
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (running) return game.Trim();
        }
        return null;
    }

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (!_settings.GameMode) return;
            var game = FindRunningGame();
            if (game != null && ActiveGame == null) Activate(game);
            else if (game == null && ActiveGame != null) Deactivate($"{ActiveGame} exited");
        }
        catch (Exception ex)
        {
            Log.Write($"Game mode check failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    void Activate(string game)
    {
        ActiveGame = game;
        ActiveSince = DateTime.Now;
        Log.Write($"Game mode: {game} started");
        StateChanged?.Invoke();

        var groups = new List<ProcessGroup>();
        if (_settings.GameModeCloseApps)
        {
            // never close the game itself, even if it was started after the snapshot
            var gameNames = _settings.Games.Select(g => g.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            groups = ProcessScanner.FindCandidates(_baseline(), _settings)
                .Where(g => !gameNames.Contains(g.ExeName)).ToList();
        }

        var options = new PrepOptions(
            groups,
            FreeMemory: _settings.GameModeFreeMemory,
            RestartExplorer: false, // restarting the shell while a game launches is too disruptive
            DnsAndTemp: false,
            BackgroundIds: _settings.GameModePauseServices ? _settings.BackgroundItemIds.ToList() : null);

        var result = GamePrep.Run(options, _ => { });
        lock (_pausedByGameMode) _pausedByGameMode.AddRange(result.PausedIds);
        _notify($"Game mode on for {game}. {result.Brief}", result.Warn ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    void Deactivate(string reason)
    {
        var game = ActiveGame;
        ActiveGame = null;
        ActiveSince = null;
        Log.Write($"Game mode: {reason}");

        List<string> ids;
        lock (_pausedByGameMode)
        {
            ids = _pausedByGameMode.ToList();
            _pausedByGameMode.Clear();
        }

        int restored = 0;
        if (_settings.GameModeRestoreOnExit)
        {
            foreach (var id in ids)
            {
                var (ok, _) = BackgroundServices.RestoreOne(id);
                restored += ok;
            }
        }
        StateChanged?.Invoke();
        _notify(restored > 0
            ? $"Game mode off ({game} closed). Background services restored: {restored}."
            : $"Game mode off ({game} closed).", ToolTipIcon.Info);
    }

    public void Dispose() => _timer.Dispose();
}
