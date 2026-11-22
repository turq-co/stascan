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
        FreeConsole(); // dotnet.exe консольный: убираем его окно
        ApplicationConfiguration.Initialize();

        if (AlreadyRunning())
        {
            MessageBox.Show("StaScan уже запущен: значок в трее рядом с часами.", "StaScan",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Через dotnet.exe манифест requireAdministrator не действует, поэтому права поднимаем сами.
        if (!IsAdministrator())
        {
            RelaunchElevated();
            return;
        }

        using var mutex = new Mutex(true, MutexName, out _);

        Application.ThreadException += (_, e) => Log.Write($"Ошибка UI: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Необработанная ошибка: {e.ExceptionObject}");

        Log.Write("──── StaScan запущен ────");
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
            return true; // мьютекс есть, но принадлежит экземпляру с правами администратора
        }
    }

    static bool IsAdministrator() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static void RelaunchElevated()
    {
        // Если автозапуск настроен, задача планировщика даёт права админа без окна UAC.
        // Если задача указывает на старый путь и ничего не запустилось, переходим на UAC.
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
            MessageBox.Show("StaScan нужны права администратора, чтобы закрывать процессы и чистить память.",
                "StaScan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
