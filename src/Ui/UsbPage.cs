namespace StaScan;

/// <summary>"USB ports" section: how many chips sit between the mouse/keyboard and the CPU.</summary>
sealed class UsbPage : UserControl, IPage
{
    readonly Label _summary;
    readonly ListView _devices;
    readonly ListView _controllers;

    public bool IsBusy => false;

    public UsbPage()
    {
        var title = Theme.PageTitle("Mouse and keyboard USB ports");
        _summary = Theme.PageHint("", 52);
        _summary.ForeColor = Theme.P.Text;

        var refresh = Theme.Primary("Refresh (after replugging)");
        refresh.Click += (_, _) => Reload();
        var toolbar = Theme.Toolbar(refresh);

        _devices = Theme.List();
        _devices.Columns.Add("Device", 220);
        _devices.Columns.Add("Type", 140);
        _devices.Columns.Add("Chips to CPU", 110, HorizontalAlignment.Center);
        _devices.Columns.Add("Path", 310);
        var devicesHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 0) };
        devicesHost.Controls.Add(_devices);

        var ctrlTitle = new Label { Text = "USB controllers", Font = Theme.Group, Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 10, 0, 0) };
        _controllers = Theme.List();
        _controllers.Columns.Add("Controller", 330);
        _controllers.Columns.Add("Location", 190);
        _controllers.Columns.Add("Platform", 250);
        var ctrlHint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            ForeColor = Theme.P.Muted,
            Padding = new Padding(0, 6, 0, 0),
            Text = "Your motherboard manual shows which ports belong to which controller.",
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
            Log.Write($"USB check failed: {ex}");
            _summary.Text = "Could not read USB devices, see the log for details.";
            return;
        }

        bool hasDirect = controllers.Any(c => c.ChipCount == 0);

        _devices.BeginUpdate();
        _devices.Items.Clear();
        foreach (var d in devices)
        {
            var path = string.Join(" → ", new[] { "device" }
                .Concat(d.Hubs.Select(h => $"[hub: {h}]"))
                .Concat(d.Controller.ChipCount > 0 ? new[] { $"[{d.Controller.KindText}]" } : Array.Empty<string>())
                .Append("CPU"));
            _devices.Items.Add(new ListViewItem(new[]
            {
                d.Name,
                string.IsNullOrEmpty(d.Kind) ? "HID device" : d.Kind,
                d.ChipCount.ToString(),
                path,
            })
            {
                BackColor = Verdict(d.ChipCount, hasDirect),
                ForeColor = Theme.P.Text,
                ToolTipText = $"VID {d.Vid} PID {d.Pid}, controller: {d.Controller.Name}",
            });
        }
        _devices.EndUpdate();

        _controllers.BeginUpdate();
        _controllers.Items.Clear();
        foreach (var c in controllers)
            _controllers.Items.Add(new ListViewItem(new[] { c.Name, $"{c.KindText} (chips: {c.ChipCount})", c.Platform }));
        _controllers.EndUpdate();

        var gaming = devices.Where(d => d.Kind.Contains("mouse") || d.Kind.Contains("keyboard")).ToList();
        var worst = gaming.Count > 0 ? gaming.Max(d => d.ChipCount) : 0;
        string verdict;
        if (gaming.Count == 0)
            verdict = "No USB mouse or keyboard found.";
        else if (worst == 0)
            verdict = "✓ Your mouse and keyboard are connected directly to the CPU. That is the best possible setup.";
        else if (!hasDirect && gaming.All(d => d.ChipCount <= 1))
            verdict = "✓ This motherboard has no CPU-direct USB ports, so connecting through the chipset is the best option here.";
        else if (gaming.Any(d => d.Hubs.Count > 0))
            verdict = "! Some devices go through a hub (in a monitor, a keyboard, the case front panel, etc.). Plug them directly into the motherboard.";
        else
            verdict = "! Your devices go through the chipset, but this motherboard has CPU-direct ports. Move the mouse and keyboard to those ports and click Refresh.";

        _summary.Text = verdict + "\n0 chips = direct to the CPU, 1 = through the chipset, 2 or more = through a hub as well.";
        Log.Write($"USB check: {string.Join("; ", devices.Select(d => $"{d.Name} ({d.Kind}) chips={d.ChipCount}"))}");
    }

    static Color Verdict(int chips, bool hasDirect) => chips switch
    {
        0 => Theme.P.RowOk,
        1 when !hasDirect => Theme.P.RowOk,
        1 => Theme.P.RowCaution,
        _ => Theme.P.RowBad,
    };
}
