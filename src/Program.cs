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
    static void Main()
    {
        FreeConsole(); // dotnet.exe is a console host: get rid of its window
        ApplicationConfiguration.Initialize();

        if (AlreadyRunning())
        {
            MessageBox.Show("StaScan is already running. Look for its icon in the notification area next to the clock.", "StaScan",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // When launched through dotnet.exe the requireAdministrator manifest does not apply, so elevate ourselves.
        if (!IsAdministrator())
        {
            RelaunchElevated();
            return;
        }

        using var mutex = new Mutex(true, MutexName, out _);

        Application.ThreadException += (_, e) => Log.Write($"UI error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Unhandled exception: {e.ExceptionObject}");

        Log.Write("──── StaScan started ────");
        Application.Run(new TrayContext());
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
            MessageBox.Show("StaScan needs administrator rights to close processes and free memory.",
                "StaScan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
