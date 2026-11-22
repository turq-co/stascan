namespace StaScan;

static class AppPaths
{
    public static string Dir { get; } = CreateDir();

    static string CreateDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StaScan");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string File(string name) => Path.Combine(Dir, name);
}

static class Log
{
    static readonly object Sync = new();
    public static string FilePath => AppPaths.File("log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000)
                    File.Move(FilePath, AppPaths.File("log.old.txt"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex)
        {
            // лог не должен ронять приложение, но причину сбоя сохраняем рядом с TEMP
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "StaScan-log-error.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex.GetType().Name}: {ex.Message}  | {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
