using System.Text.Json;

namespace StaScan;

sealed class Settings
{
    /// <summary>Executable names that are never offered for closing.</summary>
    public List<string> Whitelist { get; set; } = new()
    {
        "steam.exe", "steamwebhelper.exe", "steamservice.exe", "gameoverlayui.exe",
        "Discord.exe", "Dorion.exe",
        "NVIDIA app.exe", "NVIDIA Overlay.exe", "NVIDIA Share.exe", "nvcontainer.exe", "nvsphelper64.exe",
        "EpicGamesLauncher.exe", "EpicWebHelper.exe",
        "Battle.net.exe",
        "obs64.exe",
        "MSIAfterburner.exe", "RTSS.exe", "RTSSHooksLoader64.exe", "EncoderServer.exe",
    };

    public bool CleanMemory { get; set; } = true;
    public bool RestartExplorer { get; set; } = true;
    public bool FlushDnsAndTemp { get; set; } = true;

    /// <summary>Remove CPU 0 from cs2.exe (StaScan watches for the process while this is on).</summary>
    public bool Cs2SkipCpu0 { get; set; }

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    // ── Game mode ──
    /// <summary>Run game prep automatically when a game from <see cref="Games"/> starts.</summary>
    public bool GameMode { get; set; }
    /// <summary>Executable names that count as games.</summary>
    public List<string> Games { get; set; } = new() { "cs2.exe" };
    public bool GameModeFreeMemory { get; set; } = true;
    public bool GameModePauseServices { get; set; } = true;
    /// <summary>Also close programs started since the snapshot (off by default: no confirmation is shown).</summary>
    public bool GameModeCloseApps { get; set; }
    /// <summary>Restore the services game mode paused once the last game exits.</summary>
    public bool GameModeRestoreOnExit { get; set; } = true;

    /// <summary>Executable paths whose tray icon was already made always visible (done once per location).</summary>
    public List<string> TrayPromotedFor { get; set; } = new();

    /// <summary>Pause background services marked "Include in prep" when "Prepare for gaming" is clicked.</summary>
    public bool PauseBackground { get; set; }

    /// <summary>Which background service items to pause during game prep. All by default.</summary>
    public List<string> BackgroundItemIds { get; set; } = BackgroundServices.Catalog.Select(i => i.Id).ToList();

    static string FilePath => AppPaths.File("settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public bool IsWhitelisted(string exeName) =>
        Whitelist.Any(w => string.Equals(w.Trim(), exeName, StringComparison.OrdinalIgnoreCase));

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read settings.json, using defaults: {ex.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Log.Write($"Could not save settings.json: {ex.Message}");
        }
    }
}
