using Microsoft.Win32;

namespace StaScan;

/// <summary>A program that starts with Windows from a Run key or a Startup folder.</summary>
sealed record StartupApp(string Name, string Command, string Location, bool Enabled, RegistryKey ApprovedHive, string ApprovedKey, string ApprovedName);

/// <summary>
/// Lists and toggles startup programs exactly like Task Manager → Startup apps: entries are never deleted,
/// only marked as disabled under "Explorer\StartupApproved", so they can be re-enabled from either place.
/// </summary>
static class StartupApps
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunKey32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupApp> List()
    {
        var apps = new List<StartupApp>();
        AddRunKey(apps, Registry.CurrentUser, RunKey, "Run", "Current user, registry");
        AddRunKey(apps, Registry.LocalMachine, RunKey, "Run", "All users, registry");
        AddRunKey(apps, Registry.LocalMachine, RunKey32, "Run32", "All users, registry (32-bit)");
        AddFolder(apps, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, "Current user, Startup folder");
        AddFolder(apps, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, "All users, Startup folder");
        return apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void AddRunKey(List<StartupApp> apps, RegistryKey hive, string key, string approvedSub, string location)
    {
        using var k = hive.OpenSubKey(key);
        if (k == null) return;
        foreach (var name in k.GetValueNames())
        {
            if (string.IsNullOrEmpty(name) || k.GetValue(name) is not string command) continue;
            apps.Add(new StartupApp(name, command, location, IsEnabled(hive, approvedSub, name), hive, Approved + approvedSub, name));
        }
    }

    static void AddFolder(List<StartupApp> apps, string folder, RegistryKey hive, string location)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            apps.Add(new StartupApp(Path.GetFileNameWithoutExtension(file), file, location,
                IsEnabled(hive, "StartupFolder", fileName), hive, Approved + "StartupFolder", fileName));
        }
    }

    /// <summary>The first byte of the StartupApproved value is even when enabled (2, 6) and odd when disabled (3, 7).</summary>
    static bool IsEnabled(RegistryKey hive, string approvedSub, string name)
    {
        using var k = hive.OpenSubKey(Approved + approvedSub);
        return k?.GetValue(name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }

    public static void SetEnabled(StartupApp app, bool enabled)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4); // when it was disabled, as Task Manager records
        using var k = app.ApprovedHive.CreateSubKey(app.ApprovedKey, writable: true);
        k.SetValue(app.ApprovedName, data, RegistryValueKind.Binary);
        Log.Write($"Startup app {(enabled ? "enabled" : "disabled")}: {app.Name} ({app.Location})");
    }
}
