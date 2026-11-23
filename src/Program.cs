using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace StaScan;

static class Program
{
    const string MutexName = @"Local\StaScan.SingleInstance";

    [DllImport("kernel32.dll")]
    static extern bool FreeConsole();

    [STAThread]
    static int Main(string[] args)
    {
        FreeConsole(); // when started through dotnet.exe (a console host), get rid of its window

        // Command-line actions used by the installer and uninstaller. They run without any UI.
        if (args.Length > 0)
            return RunCommand(args[0]);

        ApplicationConfiguration.Initialize();
        L.Set(Settings.Load().Language);

        if (AlreadyRunning())
        {
            MessageBox.Show(L.T("StaScan is already running. Look for its icon in the notification area next to the clock."), "StaScan",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        // When launched through dotnet.exe the requireAdministrator manifest does not apply, so elevate ourselves.
        if (!IsAdministrator())
        {
            RelaunchElevated();
            return 0;
        }

        using var mutex = new Mutex(true, MutexName, out _);

        Application.ThreadException += (_, e) => Log.Write($"UI error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Unhandled exception: {e.ExceptionObject}");

        Log.Write($"──── StaScan {Application.ProductVersion} started from {Environment.ProcessPath} ────");
        Application.Run(new TrayContext());
        return 0;
    }

    /// <summary>
    /// --enable-autostart   create the sign-in task for the current user (installer, "Start with Windows" option)
    /// --disable-autostart  remove the sign-in task
    /// --uninstall          restore paused background services and remove both scheduled tasks
    /// </summary>
    static int RunCommand(string command)
    {
        try
        {
            switch (command.ToLowerInvariant())
            {
                case "--enable-autostart":
                    return Autostart.Enable() ? 0 : 1;
                case "--disable-autostart":
                    return Autostart.Disable() ? 0 : 1;
                case "--uninstall":
                    var (restored, errors) = BackgroundServices.Restore();
                    Log.Write($"Uninstall: restored {restored} background items, {errors.Count} errors");
                    Autostart.RemoveAll();
                    return 0;
                default:
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Command {command} failed: {ex}");
            return 1;
        }
    }

    static bool AlreadyRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName, out var m)) return false;
            m.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // the mutex exists but belongs to an elevated instance
        }
    }

    static bool IsAdministrator() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static void RelaunchElevated()
    {
        // If autostart is configured, the scheduled task grants admin rights without a UAC prompt.
        // If the task points to an old path and nothing starts, fall back to UAC.
        if (Autostart.IsEnabled && Autostart.RunMainTask())
        {
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(250);
                if (AlreadyRunning()) return;
            }
        }

        var (command, args) = Autostart.LaunchCommand();
        try
        {
            Process.Start(new ProcessStartInfo(command, args) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception)
        {
            MessageBox.Show(L.T("StaScan needs administrator rights to close processes and free memory."),
                "StaScan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
