namespace StaScan;

/// <summary>Раздел «USB-порты»: через сколько чипов мышь и клавиатура подключены к процессору.</summary>
sealed class UsbPage : UserControl, IPage
{
    readonly Label _summary;
    readonly ListView _devices;
    readonly ListView _controllers;

    public bool IsBusy => false;

    public UsbPage()
    {
        var title = Theme.PageTitle("USB-порты мыши и клавиатуры");
        _summary = Theme.PageHint("", 52);
        _summary.ForeColor = Theme.P.Text;

        var refresh = Theme.Primary("Обновить (после того как переткнули)");
        refresh.Click += (_, _) => Reload();
        var toolbar = Theme.Toolbar(refresh);

        _devices = Theme.List();
        _devices.Columns.Add("Устройство", 220);
        _devices.Columns.Add("Тип", 140);
        _devices.Columns.Add("Чипов до CPU", 110, HorizontalAlignment.Center);
        _devices.Columns.Add("Путь", 310);
        var devicesHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 0) };
        devicesHost.Controls.Add(_devices);

        var ctrlTitle = new Label { Text = "USB-контроллеры", Font = Theme.Group, Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 10, 0, 0) };
        _controllers = Theme.List();
        _controllers.Columns.Add("Контроллер", 330);
        _controllers.Columns.Add("Где находится", 190);
        _controllers.Columns.Add("Платформа", 250);
        var ctrlHint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            ForeColor = Theme.P.Muted,
            Padding = new Padding(0, 6, 0, 0),
            Text = "Какие разъёмы идут к какому контроллеру, написано в мануале платы.",
        };
        var ctrlHost = new Panel { Dock = DockStyle.Bottom, Height = 170, Padding = new Padding(20, 0, 20, 4) };
        ctrlHost.Controls.Add(_controllers);
        ctrlHost.Controls.Add(ctrlHint);
        ctrlHost.Controls.Add(ctrlTitle);

        Controls.Add(devicesHost);
        Controls.Add(ctrlHost);
        Controls.Add(toolbar);
        Controls.Add(_summary);
        Controls.Add(title);
    }

    public void OnShown() => Reload();

    void Reload()
    {
        List<UsbInputDevice> devices;
        List<UsbController> controllers;
        try
        {
            (devices, controllers) = UsbTopology.Scan();
        }
        catch (Exception ex)
        {
            Log.Write($"Проверка USB упала: {ex}");
            _summary.Text = "Не удалось прочитать USB-устройства, подробности в логе.";
            return;
        }

        bool hasDirect = controllers.Any(c => c.ChipCount == 0);

        _devices.BeginUpdate();
        _devices.Items.Clear();
        foreach (var d in devices)
        {
            var path = string.Join(" → ", new[] { "устройство" }
                .Concat(d.Hubs.Select(h => $"[хаб: {h}]"))
                .Concat(d.Controller.ChipCount > 0 ? new[] { $"[{d.Controller.KindText}]" } : Array.Empty<string>())
                .Append("CPU"));
            _devices.Items.Add(new ListViewItem(new[]
            {
                d.Name,
                string.IsNullOrEmpty(d.Kind) ? "HID-устройство" : d.Kind,
                d.ChipCount.ToString(),
                path,
            })
            {
                BackColor = Verdict(d.ChipCount, hasDirect),
                ForeColor = Theme.P.Text,
                ToolTipText = $"VID {d.Vid} PID {d.Pid}, контроллер: {d.Controller.Name}",
            });
        }
        _devices.EndUpdate();

        _controllers.BeginUpdate();
        _controllers.Items.Clear();
        foreach (var c in controllers)
            _controllers.Items.Add(new ListViewItem(new[] { c.Name, $"{c.KindText} (чипов: {c.ChipCount})", c.Platform }));
        _controllers.EndUpdate();

        var gaming = devices.Where(d => d.Kind.Contains("мышь") || d.Kind.Contains("клавиатура")).ToList();
        var worst = gaming.Count > 0 ? gaming.Max(d => d.ChipCount) : 0;
        string verdict;
        if (gaming.Count == 0)
            verdict = "Мышь и клавиатура по USB не найдены.";
        else if (worst == 0)
            verdict = "✓ Мышь и клавиатура подключены напрямую к процессору. Лучше не бывает.";
        else if (!hasDirect && gaming.All(d => d.ChipCount <= 1))
            verdict = "✓ На этой плате нет USB-портов напрямую от процессора, так что подключение через чипсет здесь лучший вариант.";
        else if (gaming.Any(d => d.Hubs.Count > 0))
            verdict = "! Часть устройств подключена через хаб (в мониторе, в клавиатуре, на передней панели корпуса и т.п.). Подключите их прямо в разъём на материнской плате.";
        else
            verdict = "! Устройства подключены через чипсет, а на плате есть порты прямо от процессора. Переткните мышь и клавиатуру в такие порты и нажмите «Обновить».";

        _summary.Text = verdict + "\n0 чипов = напрямую в CPU, 1 = через чипсет, 2 и больше = ещё и через хаб.";
        Log.Write($"Проверка USB: {string.Join("; ", devices.Select(d => $"{d.Name} ({d.Kind}) чипов={d.ChipCount}"))}");
    }

    static Color Verdict(int chips, bool hasDirect) => chips switch
    {
        0 => Theme.P.RowOk,
        1 when !hasDirect => Theme.P.RowOk,
        1 => Theme.P.RowCaution,
        _ => Theme.P.RowBad,
    };
}
