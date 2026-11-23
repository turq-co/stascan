using System.Runtime.InteropServices;

namespace StaScan;

/// <summary>
/// System-wide hotkeys, registered on a hidden message-only window:
///   Ctrl+Alt+G  open Game prep
///   Ctrl+Alt+B  quick boost (free RAM, pause marked services; nothing is closed)
///   Ctrl+Alt+M  toggle game mode
///   Ctrl+Alt+F  measure FPS of the foreground game (PresentMon)
/// </summary>
sealed class Hotkeys : NativeWindow, IDisposable
{
    public enum Action { OpenPrep = 1, QuickBoost = 2, ToggleGameMode = 3, MeasureFps = 4 }

    public static readonly (Action action, string keys, string description)[] All =
    {
        (Action.OpenPrep, "Ctrl+Alt+G", "open Game prep"),
        (Action.QuickBoost, "Ctrl+Alt+B", "quick boost: free RAM and pause marked services"),
        (Action.ToggleGameMode, "Ctrl+Alt+M", "turn game mode on or off"),
        (Action.MeasureFps, "Ctrl+Alt+F", "measure FPS of the game in the foreground for 20 seconds"),
    };

    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly Action<Action> _handler;
    readonly List<int> _registered = new();

    /// <summary>Hotkeys that could not be registered because another program already uses them.</summary>
    public List<string> Failed { get; } = new();

    public Hotkeys(Action<Action> handler)
    {
        _handler = handler;
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
    }

    public void Register()
    {
        Unregister();
        foreach (var (action, keys, _) in All)
        {
            uint vk = keys[^1];
            if (RegisterHotKey(Handle, (int)action, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, vk))
                _registered.Add((int)action);
            else
            {
                Failed.Add(keys);
                Log.Write($"Hotkey {keys} is already used by another program");
            }
        }
    }

    public void Unregister()
    {
        foreach (var id in _registered) UnregisterHotKey(Handle, id);
        _registered.Clear();
        Failed.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            try { _handler((Action)(int)m.WParam); }
            catch (Exception ex) { Log.Write($"Hotkey handler failed: {ex}"); }
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}
