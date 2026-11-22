using System.Text.Json;

namespace StaScan;

/// <summary>Снимок «чистого» набора процессов после входа в Windows.</summary>
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

    /// <summary>true, если снимок сделан в текущем сеансе Windows (после последней загрузки).</summary>
    public bool IsFromCurrentBoot => Math.Abs((BootTimeUtc - CurrentBootUtc).TotalMinutes) < 2;

    /// <summary>Берёт снимок этой загрузки из файла, если он есть, иначе делает новый.</summary>
    public static (Baseline baseline, bool reused) LoadOrTake()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var b = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(FilePath));
                if (b != null && b.IsFromCurrentBoot)
                {
                    Log.Write($"Снимок от {b.TakenAt:HH:mm} этой загрузки взят из файла ({b.Keys.Count} записей)");
                    return (b, true);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"baseline.json не прочитан: {ex.Message}");
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
            Log.Write($"Не удалось сохранить baseline.json: {ex.Message}");
        }
        Log.Write($"Сделан новый снимок: {b.Keys.Count} записей");
        return b;
    }
}
