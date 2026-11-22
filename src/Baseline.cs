using System.Text.Json;

namespace StaScan;

/// <summary>Snapshot of the "clean" set of processes taken after signing in to Windows.</summary>
sealed class Baseline
{
    public DateTime BootTimeUtc { get; set; }
    public DateTime TakenAt { get; set; }
    public List<string> Keys { get; set; } = new();

    HashSet<string>? _set;

    static string FilePath => AppPaths.File("baseline.json");

    public static DateTime CurrentBootUtc =>
        DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public bool Contains(string key)
    {
        _set ??= new HashSet<string>(Keys, StringComparer.OrdinalIgnoreCase);
        return _set.Contains(key);
    }

    /// <summary>True if the snapshot was taken during the current Windows session (after the last boot).</summary>
    public bool IsFromCurrentBoot => Math.Abs((BootTimeUtc - CurrentBootUtc).TotalMinutes) < 2;

    /// <summary>Reuses this boot's snapshot from disk if there is one, otherwise takes a new one.</summary>
    public static (Baseline baseline, bool reused) LoadOrTake()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var b = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(FilePath));
                if (b != null && b.IsFromCurrentBoot)
                {
                    Log.Write($"Reusing snapshot from {b.TakenAt:HH:mm} of this boot ({b.Keys.Count} entries)");
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

    public static Baseline Take()
    {
        var b = new Baseline
        {
            BootTimeUtc = CurrentBootUtc,
            TakenAt = DateTime.Now,
            Keys = ProcessScanner.Snapshot().Select(p => p.Key).Distinct().OrderBy(k => k).ToList(),
        };
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(b, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write($"Could not save baseline.json: {ex.Message}");
        }
        Log.Write($"New snapshot taken: {b.Keys.Count} entries");
        return b;
    }
}
