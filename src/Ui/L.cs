using System.Globalization;
using System.Text.Json;

namespace StaScan;

enum UiLanguage { System, English, Russian }

/// <summary>
/// UI translations. The English text is the key, so the code stays readable and anything
/// without a translation falls back to English. Translations live in assets/i18n/&lt;lang&gt;.json
/// (embedded in the executable). Log messages are intentionally not translated.
/// </summary>
static class L
{
    static Dictionary<string, string> _map = new();

    public static string Code { get; private set; } = "en";

    public static void Set(UiLanguage language)
    {
        Code = language switch
        {
            UiLanguage.Russian => "ru",
            UiLanguage.English => "en",
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en",
        };
        _map = Code == "en" ? new() : Load(Code);
    }

    static Dictionary<string, string> Load(string code)
    {
        try
        {
            using var stream = typeof(L).Assembly.GetManifestResourceStream($"i18n.{code}.json");
            if (stream == null) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not load translation {code}: {ex.Message}");
            return new();
        }
    }

    /// <summary>Translates a fixed text.</summary>
    public static string T(string en) => _map.TryGetValue(en, out var t) ? t : en;

    static CultureInfo Culture => Code == "ru" ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.InvariantCulture;

    /// <summary>"Nov 23" / "23 нояб."</summary>
    public static string Day(DateTime d) => d.ToString(Code == "ru" ? "d MMM" : "MMM d", Culture);
    /// <summary>"Nov 23, 19:24" / "23 нояб., 19:24"</summary>
    public static string DayTime(DateTime d) => d.ToString(Code == "ru" ? "d MMM, HH:mm" : "MMM d, HH:mm", Culture);
    /// <summary>"Dec 27, 2026" / "27 дек. 2026"</summary>
    public static string Date(DateTime d) => d.ToString(Code == "ru" ? "d MMM yyyy" : "MMM d, yyyy", Culture);
    /// <summary>"Nov 23, 2026 19:24" / "23 нояб. 2026, 19:24"</summary>
    public static string Full(DateTime d) => d.ToString(Code == "ru" ? "d MMM yyyy, HH:mm" : "MMM d, yyyy HH:mm", Culture);

    /// <summary>Translates a text with {0}, {1}… placeholders and fills them in.</summary>
    public static string F(string en, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, T(en), args);
}
