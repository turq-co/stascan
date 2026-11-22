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
