using System.Text.Json;

namespace StaScan;

/// <summary>
/// Snapshot of the "clean" set of processes. By default it is taken after signing in and lives until the next reboot.
/// A pinned snapshot is kept across reboots until it is unpinned or retaken. This works because processes are
/// compared by executable path, not by PID.
/// </summary>
sealed class Baseline
{
    public DateTime BootTimeUtc { get; set; }
    public DateTime TakenAt { get; set; }
    public bool Pinned { get; set; }
    public List<string> Keys { get; set; } = new();

    HashSet<string>? _set;

    static string FilePath => AppPaths.File("baseline.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static DateTime CurrentBootUtc =>
        DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public bool Contains(string key)
    {
        _set ??= new HashSet<string>(Keys, StringComparer.OrdinalIgnoreCase);
        return _set.Contains(key);
    }

    /// <summary>True if the snapshot was taken during the current Windows session (after the last boot).</summary>
    public bool IsFromCurrentBoot => Math.Abs((BootTimeUtc - CurrentBootUtc).TotalMinutes) < 2;

    /// <summary>Reuses the saved snapshot if it is pinned or from this boot, otherwise takes a new one.</summary>
    public static (Baseline baseline, bool reused) LoadOrTake()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var b = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(FilePath));
                if (b != null && (b.Pinned || b.IsFromCurrentBoot))
                {
                    Log.Write($"Reusing {(b.Pinned ? "pinned " : "")}snapshot from {b.TakenAt:yyyy-MM-dd HH:mm} ({b.Keys.Count} entries)");
                    return (b, true);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read baseline.json: {ex.Message}");
        }
        return (Take(), false);
    }

    /// <summary>Takes a new snapshot of the running processes. <paramref name="pinned"/> keeps it across reboots.</summary>
    public static Baseline Take(bool pinned = false)
    {
        var b = new Baseline
        {
            BootTimeUtc = CurrentBootUtc,
            TakenAt = DateTime.Now,
            Pinned = pinned,
            Keys = ProcessScanner.Snapshot().Select(p => p.Key).Distinct().OrderBy(k => k).ToList(),
        };
        b.Save();
        Log.Write($"New {(pinned ? "pinned " : "")}snapshot taken: {b.Keys.Count} entries");
        return b;
    }

    public void SetPinned(bool pinned)
    {
        if (Pinned == pinned) return;
        Pinned = pinned;
        Save();
        Log.Write(pinned ? "Snapshot pinned: it will be kept across reboots" : "Snapshot unpinned: a new one will be taken after the next reboot");
    }

    void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Log.Write($"Could not save baseline.json: {ex.Message}");
        }
    }
}
