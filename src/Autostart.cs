using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace StaScan;

/// <summary>
/// Task Scheduler tasks:
///  StaScan         — starts at sign-in with highest privileges (no UAC prompt), 60 s delay;
///  StaScanExplorer — starts explorer.exe with normal rights (fallback for restarting Explorer).
/// </summary>
static class Autostart
{
    const string MainTask = "StaScan";
    const string ExplorerTask = "StaScanExplorer";

    static string User => WindowsIdentity.GetCurrent().Name;

    public static bool IsEnabled => Schtasks($"/query /tn \"{MainTask}\"") == 0;

    /// <summary>
    /// How to launch StaScan. Usually dotnet.exe + StaScan.dll: dotnet.exe is signed by Microsoft
    /// and passes Smart App Control, while an unsigned StaScan.exe may be blocked.
    /// </summary>
    public static (string command, string args) LaunchCommand()
    {
        var proc = Environment.ProcessPath ?? Application.ExecutablePath;
        if (Path.GetFileName(proc).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return (proc, $"\"{Path.Combine(AppContext.BaseDirectory, "StaScan.dll")}\"");
        return (proc, "");
    }

    /// <summary>Runs the autostart task: this gives StaScan admin rights without a UAC prompt.</summary>
    public static bool RunMainTask() => Schtasks($"/run /tn \"{MainTask}\"") == 0;

    public static bool Enable()
    {
        var (exe, exeArgs) = LaunchCommand();
        var xml = TaskXml(
            trigger: $"""
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{Esc(User)}</UserId>
                  <Delay>PT1M</Delay>
                </LogonTrigger>
              </Triggers>
            """,
            runLevel: "HighestAvailable",
            command: exe,
            args: exeArgs);
        bool ok = CreateTask(MainTask, xml);
        Log.Write(ok ? $"Autostart enabled: {exe} {exeArgs}" : "Could not enable autostart");
        return ok;
    }

    public static bool Disable()
    {
        bool ok = Schtasks($"/delete /tn \"{MainTask}\" /f") == 0;
        Log.Write(ok ? "Autostart disabled" : "Could not disable autostart");
        return ok;
    }

    /// <summary>If autostart is enabled, refreshes the launch path (in case the app was moved).</summary>
    public static void RefreshIfEnabled()
    {
        if (IsEnabled) Enable();
    }

    public static bool RunExplorerTask()
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var xml = TaskXml(trigger: "", runLevel: "LeastPrivilege", command: explorer);
        return CreateTask(ExplorerTask, xml) && Schtasks($"/run /tn \"{ExplorerTask}\"") == 0;
    }

    static string TaskXml(string trigger, string runLevel, string command, string args = "") => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>StaScan: get the PC ready for gaming</Description></RegistrationInfo>
        {trigger}
          <Principals>
            <Principal id="Author">
              <UserId>{Esc(User)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>{runLevel}</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author">
            <Exec><Command>{Esc(command)}</Command>{(args.Length > 0 ? $"<Arguments>{Esc(args)}</Arguments>" : "")}</Exec>
          </Actions>
        </Task>
        """;

    static bool CreateTask(string name, string xml)
    {
        var file = AppPaths.File($"{name}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode); // schtasks expects UTF-16
            return Schtasks($"/create /tn \"{name}\" /xml \"{file}\" /f") == 0;
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    static int Schtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            if (p.ExitCode != 0 && !args.StartsWith("/query"))
                Log.Write($"schtasks {args} → exit code {p.ExitCode}: {err.Trim()}");
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Write($"Could not start schtasks: {ex.Message}");
            return -1;
        }
    }

    static string Esc(string s) => SecurityElement.Escape(s) ?? s;
}
