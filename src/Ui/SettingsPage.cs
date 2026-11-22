using System.Diagnostics;

namespace StaScan;

/// <summary>Раздел «Настройки»: тема, снимок, автозапуск, белый список, лог.</summary>
sealed class SettingsPage : UserControl, IPage
{
    readonly AppServices _app;
    readonly Label _snapshot;
    readonly CheckBox _autostart;
    readonly TextBox _whitelist;
    readonly Label _saved;
    readonly RadioButton _themeSystem, _themeLight, _themeDark;
    bool _loading;

    public bool IsBusy => false;

    public SettingsPage(AppServices app)
    {
        _app = app;

        var title = Theme.PageTitle("Настройки");
        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(20, 0, 20, 16),
        };

        // Тема
        body.Controls.Add(Theme.Section("Оформление"));
        _themeSystem = Radio("Как в Windows");
        _themeLight = Radio("Светлая");
        _themeDark = Radio("Тёмная");
        var themeLine = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        themeLine.Controls.AddRange(new Control[] { _themeSystem, _themeLight, _themeDark });
        body.Controls.Add(themeLine);

        // Снимок
        body.Controls.Add(Theme.Section("Снимок «чистого» состояния"));
        _snapshot = new Label { AutoSize = true, ForeColor = Theme.P.Muted, Margin = new Padding(0, 0, 0, 8) };
        body.Controls.Add(_snapshot);
        var retake = Theme.Secondary("Сделать снимок заново (текущее состояние = чистое)");
        retake.Click += (_, _) => { _app.RetakeBaseline(); OnShown(); };
        body.Controls.Add(retake);

        // Автозапуск
        body.Controls.Add(Theme.Section("Запуск"));
        _autostart = Theme.Check("Запускать при входе в Windows (без окна UAC, через планировщик задач)", false);
        _autostart.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            bool ok = _autostart.Checked ? Autostart.Enable() : Autostart.Disable();
            if (!ok)
            {
                _app.Notify("Не получилось изменить автозапуск, подробности в логе.", ToolTipIcon.Warning);
                _loading = true;
                _autostart.Checked = Autostart.IsEnabled;
                _loading = false;
            }
        };
        body.Controls.Add(_autostart);

        // Белый список
        body.Controls.Add(Theme.Section("Белый список"));
        body.Controls.Add(new Label
        {
            Text = "Эти программы никогда не предлагаются к закрытию. Одно имя exe на строку, например steam.exe",
            AutoSize = true,
            ForeColor = Theme.P.Muted,
        });
        _whitelist = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.Mono,
            Width = 440,
            Height = 200,
            Margin = new Padding(0, 6, 0, 8),
        };
        body.Controls.Add(_whitelist);
        var save = Theme.Primary("Сохранить белый список");
        _saved = new Label { AutoSize = true, ForeColor = Theme.P.Accent, Margin = new Padding(12, 9, 0, 0) };
        save.Click += (_, _) => SaveWhitelist();
        var saveLine = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        saveLine.Controls.AddRange(new Control[] { save, _saved });
        body.Controls.Add(saveLine);

        // Лог
        body.Controls.Add(Theme.Section("Лог и данные"));
        var openLog = Theme.Secondary("Открыть папку с логом");
        openLog.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Dir}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write($"Не открыть папку: {ex.Message}"); }
        };
        body.Controls.Add(openLog);

        Controls.Add(body);
        Controls.Add(title);
    }

    RadioButton Radio(string text)
    {
        var rb = new RadioButton { Text = text, AutoSize = true, Margin = new Padding(0, 2, 18, 2) };
        rb.CheckedChanged += (_, _) =>
        {
            if (_loading || !rb.Checked) return;
            var mode = rb == _themeDark ? ThemeMode.Dark : rb == _themeLight ? ThemeMode.Light : ThemeMode.System;
            if (mode == _app.Settings.Theme) return;
            // пересоздание окна лучше делать после выхода из обработчика клика
            BeginInvoke(() => _app.ChangeTheme(mode, Page.Settings));
        };
        return rb;
    }

    public void OnShown()
    {
        _loading = true;
        var b = _app.GetBaseline();
        _snapshot.Text = $"Сделан {b.TakenAt:dd.MM.yyyy HH:mm}, процессов в нём: {b.Keys.Count}. " +
                         "Всё, что запущено позже, предлагается закрыть при подготовке к игре.";
        _autostart.Checked = Autostart.IsEnabled;
        _whitelist.Text = string.Join(Environment.NewLine, _app.Settings.Whitelist);
        _saved.Text = "";
        _themeSystem.Checked = _app.Settings.Theme == ThemeMode.System;
        _themeLight.Checked = _app.Settings.Theme == ThemeMode.Light;
        _themeDark.Checked = _app.Settings.Theme == ThemeMode.Dark;
        _loading = false;
    }

    void SaveWhitelist()
    {
        _app.Settings.Whitelist = _whitelist.Lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _app.Settings.Save();
        Log.Write($"Белый список обновлён: {_app.Settings.Whitelist.Count} записей");
        _saved.Text = "Сохранено";
    }
}
