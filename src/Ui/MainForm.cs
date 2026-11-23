namespace StaScan;

enum Page { Prepare, GameMode, Services, Startup, Diagnostics, Usb, Settings }

/// <summary>Shared by all sections of the window: settings, snapshot, notifications.</summary>
sealed class AppServices
{
    public required Settings Settings { get; init; }
    public required Func<Baseline> GetBaseline { get; init; }
    public required Action RetakeBaseline { get; init; }
    /// <summary>Pin the snapshot (keep it across reboots) or unpin it.</summary>
    public required Action<bool> SetBaselinePinned { get; init; }
    public required Action<string, ToolTipIcon> Notify { get; init; }
    public required Action Cs2SettingChanged { get; init; }
    /// <summary>Change the theme: the window is recreated and reopened on the given section.</summary>
    public required Action<ThemeMode, Page> ChangeTheme { get; init; }

    /// <summary>Game mode settings were changed in the window.</summary>
    public required Action GameModeSettingsChanged { get; init; }
    /// <summary>Memory history sampled in the background since StaScan started.</summary>
    public required MetricsHistory Metrics { get; init; }
    /// <summary>Turn the global hotkeys on or off; returns the hotkeys another program already uses.</summary>
    public required Func<bool, List<string>> SetHotkeys { get; init; }
    public required Func<List<string>> HotkeyFailures { get; init; }
    /// <summary>The game that activated game mode and since when, or (null, null).</summary>
    public required Func<(string? game, DateTime? since)> GetActiveGame { get; init; }
    /// <summary>Raised (not on the UI thread) when a game starts or stops.</summary>
    public event Action? GameModeChanged;
    public void RaiseGameModeChanged() => GameModeChanged?.Invoke();
}

interface IPage
{
    /// <summary>Called every time the section is opened.</summary>
    void OnShown();
    bool IsBusy { get; }
}

/// <summary>Main window: sections on the left, content on the right.</summary>
sealed class MainForm : Form
{
    readonly AppServices _app;
    readonly Panel _content;
    readonly Dictionary<Page, Control> _pages = new();
    readonly Dictionary<Page, UiButton> _nav = new();

    public MainForm(AppServices app)
    {
        _app = app;
        var P = Theme.P;
        Text = "StaScan";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1080, 700);
        MinimumSize = new Size(840, 540);
        Font = Theme.Base;
        BackColor = P.Bg;
        ForeColor = P.Text;
        Icon = TrayIcon.Create();
        Theme.ApplyTitleBar(this);

        var nav = new Panel { Dock = DockStyle.Left, Width = 210, BackColor = P.Surface, Padding = new Padding(8, 12, 8, 8) };
        var brand = new Label
        {
            Text = "StaScan",
            Font = new Font("Segoe UI Semibold", 14f),
            Dock = DockStyle.Top,
            Height = 50,
            Padding = new Padding(12, 6, 0, 0),
        };

        var items = new (Page page, string text)[]
        {
            (Page.Prepare, "Game prep"),
            (Page.GameMode, "Game mode"),
            (Page.Services, "Background services"),
            (Page.Startup, "Startup apps"),
            (Page.Diagnostics, "Diagnostics"),
            (Page.Usb, "USB ports"),
            (Page.Settings, "Settings"),
        };
        // Dock = Top stacks from the bottom up, so add the items in reverse order
        foreach (var (page, text) in items.Reverse())
        {
            var b = new UiButton(text, ButtonKind.Nav) { Dock = DockStyle.Top };
            b.Click += (_, _) => ShowPage(page);
            _nav[page] = b;
            nav.Controls.Add(b);
            nav.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 2, BackColor = P.Surface }); // gap between items
        }
        nav.Controls.Add(brand);

        _content = new Panel { Dock = DockStyle.Fill, BackColor = P.Bg };
        Controls.Add(_content);
        Controls.Add(nav);

        FormClosing += (_, e) =>
        {
            if (_pages.Values.OfType<IPage>().Any(p => p.IsBusy))
            {
                e.Cancel = true;
                MessageBox.Show(this, "Please wait, StaScan is still working.", "StaScan", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
    }

    public bool IsBusy => _pages.Values.OfType<IPage>().Any(p => p.IsBusy);

    public void ShowPage(Page page)
    {
        if (!_pages.TryGetValue(page, out var control))
        {
            control = page switch
            {
                Page.Prepare => new PreparePage(_app, () => ShowPage(Page.Usb)),
                Page.GameMode => new GameModePage(_app),
                Page.Services => new ServicesPage(_app),
                Page.Startup => new StartupPage(),
                Page.Diagnostics => new DiagnosticsPage(_app),
                Page.Usb => new UsbPage(),
                _ => new SettingsPage(_app),
            };
            control.Dock = DockStyle.Fill;
            control.Visible = false;
            control.BackColor = Theme.P.Bg;
            control.ForeColor = Theme.P.Text;
            Theme.Style(control);
            _pages[page] = control;
            _content.Controls.Add(control);
        }

        foreach (var (p, c) in _pages) c.Visible = p == page;
        foreach (var (p, b) in _nav) b.Selected = p == page;
        ((IPage)control).OnShown();
    }

    public void Open(Page page)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        ShowPage(page);
        Activate();
    }
}
