namespace StaScan;

enum Page { Prepare, Services, Usb, Settings }

/// <summary>Общее для всех разделов окна: настройки, снимок, уведомления.</summary>
sealed class AppServices
{
    public required Settings Settings { get; init; }
    public required Func<Baseline> GetBaseline { get; init; }
    public required Action RetakeBaseline { get; init; }
    public required Action<string, ToolTipIcon> Notify { get; init; }
    public required Action Cs2SettingChanged { get; init; }
    /// <summary>Сменить тему: окно пересоздаётся и открывается на указанном разделе.</summary>
    public required Action<ThemeMode, Page> ChangeTheme { get; init; }
}

interface IPage
{
    /// <summary>Вызывается при каждом открытии раздела.</summary>
    void OnShown();
    bool IsBusy { get; }
}

/// <summary>Главное окно: разделы слева, содержимое справа.</summary>
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
            (Page.Prepare, "Подготовка к игре"),
            (Page.Services, "Фоновые службы"),
            (Page.Usb, "USB-порты"),
            (Page.Settings, "Настройки"),
        };
        // Dock = Top складывает снизу вверх, поэтому добавляем в обратном порядке
        foreach (var (page, text) in items.Reverse())
        {
            var b = new UiButton(text, ButtonKind.Nav) { Dock = DockStyle.Top };
            b.Click += (_, _) => ShowPage(page);
            _nav[page] = b;
            nav.Controls.Add(b);
            nav.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 2, BackColor = P.Surface }); // зазор между пунктами
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
                MessageBox.Show(this, "Подождите, StaScan ещё выполняет действие.", "StaScan", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                Page.Services => new ServicesPage(_app),
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
