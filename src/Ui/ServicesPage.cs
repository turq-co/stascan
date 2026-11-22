namespace StaScan;

/// <summary>Раздел «Фоновые службы»: у каждой службы свой переключатель.</summary>
sealed class ServicesPage : UserControl, IPage
{
    readonly AppServices _app;
    readonly FlowLayoutPanel _rows;
    readonly Label _status;
    readonly UiButton _pauseSelected, _restoreAll, _refresh;
    readonly CheckBox _withPrepare;
    readonly List<ServiceRow> _rowList = new();
    int _busy;

    public bool IsBusy => _busy > 0;

    public ServicesPage(AppServices app)
    {
        _app = app;
        var P = Theme.P;

        var title = Theme.PageTitle("Фоновые службы Windows");
        var hint = Theme.PageHint(
            "Переключатель выключает службу на время игры: после перезагрузки StaScan вернёт её сам. " +
            "Через «⋯» можно отключить насовсем.", 40);

        _withPrepare = Theme.Check("Приостанавливать службы с галкой «В подготовке» при нажатии «Подготовить к игре»", app.Settings.PauseBackground);
        _withPrepare.CheckedChanged += (_, _) =>
        {
            _app.Settings.PauseBackground = _withPrepare.Checked;
            _app.Settings.Save();
        };
        _withPrepare.Location = new Point(20, 6);
        var optionHost = new Panel { Dock = DockStyle.Top, Height = 36 };
        optionHost.Controls.Add(_withPrepare);

        _pauseSelected = Theme.Primary("Приостановить отмеченные сейчас");
        _restoreAll = Theme.Secondary("Вернуть всё как было");
        _refresh = Theme.Secondary("Обновить");
        _pauseSelected.Click += async (_, _) => await RunBulk(() => BackgroundServices.Apply(_app.Settings.BackgroundItemIds, PauseMode.Temporary), "Приостановлено");
        _restoreAll.Click += async (_, _) => await RunBulk(() => BackgroundServices.Restore(), "Возвращено");
        _refresh.Click += (_, _) => RefreshAll();
        var toolbar = Theme.Toolbar(_pauseSelected, _restoreAll, _refresh);

        _rows = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16, 0, 16, 12),
        };
        _rows.SizeChanged += (_, _) => FitRows();

        _status = new Label { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(20, 8, 20, 0), ForeColor = P.Muted };

        string? group = null;
        foreach (var item in BackgroundServices.Available)
        {
            if (item.Group != group)
            {
                group = item.Group;
                _rows.Controls.Add(new Label { Text = group, Font = Theme.Group, Height = 36, Padding = new Padding(4, 14, 0, 0), Margin = new Padding(0) });
            }
            var row = new ServiceRow(item, app.Settings.BackgroundItemIds.Contains(item.Id));
            row.ToggleRequested += async () => await RunOne(() => BackgroundServices.Toggle(item.Id));
            row.PermanentRequested += async () =>
            {
                if (Confirm($"«{item.Title}» будет отключено и не включится после перезагрузки, пока вы не вернёте его здесь или в трее. Продолжить?"))
                    await RunOne(() =>
                    {
                        var (_, errors) = BackgroundServices.Apply(new[] { item.Id }, PauseMode.Permanent);
                        if (errors.Count > 0) throw new InvalidOperationException(errors[0]);
                        return $"{item.Title}: отключено насовсем";
                    });
            };
            row.RestoreRequested += async () => await RunOne(() =>
            {
                var (ok, errors) = BackgroundServices.RestoreOne(item.Id);
                if (errors.Count > 0) throw new InvalidOperationException(errors[0]);
                return ok > 0 ? $"{item.Title}: возвращено как было" : $"{item.Title}: StaScan его не менял";
            });
            row.InPrepareChanged += on =>
            {
                var ids = _app.Settings.BackgroundItemIds;
                ids.Remove(item.Id);
                if (on) ids.Add(item.Id);
                _app.Settings.Save();
            };
            _rowList.Add(row);
            _rows.Controls.Add(row);
        }

        Controls.Add(_rows);
        Controls.Add(_status);
        Controls.Add(toolbar);
        Controls.Add(optionHost);
        Controls.Add(hint);
        Controls.Add(title);

        BackgroundServices.Changed += OnChanged;
        Disposed += (_, _) => BackgroundServices.Changed -= OnChanged;
    }

    public void OnShown()
    {
        _withPrepare.Checked = _app.Settings.PauseBackground;
        RefreshAll();
    }

    void OnChanged()
    {
        if (IsDisposed || !IsHandleCreated || !Visible) return;
        BeginInvoke(RefreshAll);
    }

    void FitRows()
    {
        int w = _rows.ClientSize.Width - _rows.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        foreach (Control c in _rows.Controls) c.Width = Math.Max(400, w);
    }

    /// <summary>Состояние читаем в фоне: опрос служб и планировщика занимает заметное время.</summary>
    async void RefreshAll()
    {
        if (IsBusy) return;
        var data = await Task.Run(() =>
        {
            var state = BackgroundServices.LoadState();
            return _rowList.Select(r =>
            {
                string now;
                try { now = r.Item.Describe(); } catch (Exception ex) { now = "ошибка: " + ex.Message; }
                return (row: r, status: BackgroundServices.GetStatus(r.Item, state), now, mark: BackgroundServices.PausedMark(r.Item, state));
            }).ToList();
        });
        if (IsDisposed) return;
        foreach (var (row, status, now, mark) in data) row.SetState(status, now, mark);
        _restoreAll.Enabled = !IsBusy && data.Any(d => d.status is BgStatus.PausedForGame or BgStatus.DisabledPermanently);
        FitRows();
    }

    async Task RunOne(Func<string> action)
    {
        SetBusy(true);
        try
        {
            var text = await Task.Run(action);
            _status.ForeColor = Theme.P.Muted;
            _status.Text = text;
        }
        catch (Exception ex)
        {
            _status.ForeColor = Theme.P.Warn;
            _status.Text = "Не получилось: " + ex.Message;
            Log.Write($"Службы: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            RefreshAll();
        }
    }

    async Task RunBulk(Func<(int ok, List<string> errors)> action, string doneText)
    {
        SetBusy(true);
        try
        {
            var (ok, errors) = await Task.Run(action);
            _status.ForeColor = errors.Count > 0 ? Theme.P.Warn : Theme.P.Muted;
            _status.Text = $"{doneText}: {ok}." + (errors.Count > 0 ? "  Не получилось: " + string.Join("; ", errors) : "");
            _app.Notify($"{doneText}: {ok}." + (errors.Count > 0 ? $" Ошибок: {errors.Count}." : ""),
                errors.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        finally
        {
            SetBusy(false);
            RefreshAll();
        }
    }

    void SetBusy(bool busy)
    {
        _busy += busy ? 1 : -1;
        bool b = IsBusy;
        foreach (var c in new Control[] { _pauseSelected, _restoreAll, _refresh }) c.Enabled = !b;
        foreach (var r in _rowList) r.Enabled = !b;
        UseWaitCursor = b;
        if (b) { _status.ForeColor = Theme.P.Muted; _status.Text = "Выполняю…"; }
    }

    bool Confirm(string text) =>
        MessageBox.Show(FindForm(), text, "StaScan", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
}

/// <summary>Строка службы: переключатель, название с описанием, состояние, галка «В подготовке», меню «⋯».</summary>
sealed class ServiceRow : UserControl
{
    public BgItem Item { get; }
    public event Action? ToggleRequested, PermanentRequested, RestoreRequested;
    public event Action<bool>? InPrepareChanged;

    readonly ToggleSwitch _switch;
    readonly Label _state;
    readonly ToolStripMenuItem _permItem, _restoreItem;
    readonly ToolTip _tip = new();

    public ServiceRow(BgItem item, bool inPrepare)
    {
        Item = item;
        var P = Theme.P;
        Height = 70;
        Margin = new Padding(0, 0, 0, 2);

        _switch = new ToggleSwitch { Anchor = AnchorStyles.Left };
        _switch.Click += (_, _) => ToggleRequested?.Invoke();

        var title = new Label { Text = item.Title, Font = Theme.Bold, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        var desc = new Label { Text = item.Description, Font = Theme.Small, ForeColor = P.Muted, AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true };
        var text = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(0) };
        text.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        text.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        text.Controls.Add(title, 0, 0);
        text.Controls.Add(desc, 0, 1);

        _state = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Small };

        var prep = Theme.Check("В подготовке", inPrepare);
        prep.Anchor = AnchorStyles.Left;
        prep.CheckedChanged += (_, _) => InPrepareChanged?.Invoke(prep.Checked);
        _tip.SetToolTip(prep, "Приостанавливать этот пункт кнопкой «Подготовить к игре» (если это включено вверху раздела)");

        var menu = new ContextMenuStrip { Renderer = Theme.MenuRenderer() };
        _permItem = new ToolStripMenuItem("Отключить насовсем…", null, (_, _) => PermanentRequested?.Invoke());
        _restoreItem = new ToolStripMenuItem("Вернуть как было", null, (_, _) => RestoreRequested?.Invoke());
        menu.Items.Add(_permItem);
        menu.Items.Add(_restoreItem);
        var more = new UiButton("⋯", ButtonKind.Icon) { Anchor = AnchorStyles.None };
        more.Click += (_, _) => menu.Show(more, new Point(0, more.Height));

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Padding = new Padding(4, 0, 4, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        grid.Controls.Add(_switch, 0, 0);
        grid.Controls.Add(text, 1, 0);
        grid.Controls.Add(_state, 2, 0);
        grid.Controls.Add(prep, 3, 0);
        grid.Controls.Add(more, 4, 0);
        Controls.Add(grid);

        Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.P.Line);
            e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        };
    }

    public void SetState(BgStatus status, string now, string? mark)
    {
        var P = Theme.P;
        _switch.Checked = status == BgStatus.Active;
        _state.ForeColor = status switch
        {
            BgStatus.Active => P.Text,
            BgStatus.OffInWindows => P.Muted,
            _ => P.Accent,
        };
        _state.Text = status switch
        {
            BgStatus.PausedForGame or BgStatus.DisabledPermanently => $"{mark}\n{now}",
            BgStatus.OffInWindows => $"отключено в Windows\n{now}",
            _ => now,
        };
        _permItem.Enabled = status != BgStatus.DisabledPermanently && status != BgStatus.OffInWindows;
        _restoreItem.Enabled = status is BgStatus.PausedForGame or BgStatus.DisabledPermanently;
        _tip.SetToolTip(_switch, status == BgStatus.OffInWindows
            ? "Отключено не через StaScan. Включить = вернуть значение Windows по умолчанию"
            : "Вкл. = работает. Выключить = приостановить на время игры");
    }
}
