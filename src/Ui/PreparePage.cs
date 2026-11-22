using System.Diagnostics;
using System.Text;

namespace StaScan;

/// <summary>Раздел «Подготовка к игре»: новые программы с момента снимка и дополнительные действия.</summary>
sealed class PreparePage : UserControl, IPage
{
    const ulong NonpagedWarnBytes = 1536UL * 1024 * 1024; // 1.5 ГБ: выше этого похоже на утечку драйвера

    readonly AppServices _app;
    readonly Label _header;
    readonly ListView _list;
    readonly CheckBox _memory, _explorer, _dnsTemp, _cs2Affinity;
    readonly Label _bgNote;
    readonly Label _status;
    readonly ProgressBar _progress;
    readonly UiButton _run, _refresh, _checkAll, _checkNone;
    bool _busy;

    public bool IsBusy => _busy;
    Settings S => _app.Settings;

    public PreparePage(AppServices app, Action openUsb)
    {
        _app = app;

        var title = Theme.PageTitle("Подготовка к игре");
        _header = Theme.PageHint("", 44);

        _list = Theme.List(checkBoxes: true);
        _list.Columns.Add("Программа", 220);
        _list.Columns.Add("Процессов", 90, HorizontalAlignment.Right);
        _list.Columns.Add("ОЗУ", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Путь", 340);
        var itemMenu = new ContextMenuStrip { Renderer = Theme.MenuRenderer() };
        itemMenu.Items.Add("Добавить в белый список (больше не предлагать)", null, (_, _) => WhitelistSelected());
        itemMenu.Items.Add("Открыть расположение файла", null, (_, _) => RevealSelected());
        _list.ContextMenuStrip = itemMenu;
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 0) };
        listHost.Controls.Add(_list);

        _checkAll = Theme.Secondary("Отметить все");
        _checkNone = Theme.Secondary("Снять все");
        _refresh = Theme.Secondary("Обновить список");
        var usb = Theme.Secondary("Проверить USB-порты");
        _checkAll.Click += (_, _) => SetAllChecked(true);
        _checkNone.Click += (_, _) => SetAllChecked(false);
        _refresh.Click += (_, _) => LoadCandidates();
        usb.Click += (_, _) => openUsb();
        var listButtons = Theme.Toolbar(_checkAll, _checkNone, _refresh, usb);
        listButtons.Dock = DockStyle.Bottom;
        listButtons.Padding = new Padding(16, 8, 16, 0);

        _memory = Theme.Check("Очистить ОЗУ (рабочие наборы, standby list, кэш файлов)", S.CleanMemory);
        _explorer = Theme.Check("Перезапустить проводник (explorer.exe)", S.RestartExplorer);
        _dnsTemp = Theme.Check("Сбросить DNS-кэш и удалить старые файлы из %TEMP%", S.FlushDnsAndTemp);
        _cs2Affinity = Theme.Check("Отключить CPU 0 для процесса CS2 (сработает и при запуске игры позже)", S.Cs2SkipCpu0);
        _bgNote = new Label { AutoSize = true, ForeColor = Theme.P.Muted, Margin = new Padding(0, 6, 0, 0) };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(20, 0, 20, 4),
        };
        actions.Controls.AddRange(new Control[] { Theme.Section("Что ещё сделать"), _memory, _explorer, _dnsTemp, _cs2Affinity, _bgNote });

        _status = new Label { Dock = DockStyle.Fill, Padding = new Padding(20, 8, 20, 0) };
        _progress = new ProgressBar { Dock = DockStyle.Top, Height = 4, Style = ProgressBarStyle.Marquee, Visible = false };
        _run = Theme.Primary("Подготовить к игре");
        _run.Click += async (_, _) => await RunAsync();
        var runHost = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(16, 10, 16, 10) };
        runHost.Controls.Add(_run);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 84 };
        bottom.Controls.Add(_status);
        bottom.Controls.Add(runHost);
        bottom.Controls.Add(_progress);

        Controls.Add(listHost);
        Controls.Add(listButtons);
        Controls.Add(actions);
        Controls.Add(bottom);
        Controls.Add(_header);
        Controls.Add(title);
    }

    public void OnShown()
    {
        if (_busy) return;
        _cs2Affinity.Checked = S.Cs2SkipCpu0;
        int n = S.BackgroundItemIds.Count;
        _bgNote.Text = S.PauseBackground
            ? $"Также будут приостановлены фоновые службы, отмеченные в разделе «Фоновые службы» ({n})."
            : "Фоновые службы при подготовке не трогаются (включается в разделе «Фоновые службы»).";
        LoadCandidates();
    }

    void LoadCandidates()
    {
        var baseline = _app.GetBaseline();
        var groups = ProcessScanner.FindCandidates(baseline, S);
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var g in groups)
            _list.Items.Add(new ListViewItem(new[] { g.ExeName, g.Pids.Count.ToString(), Native.Mb((ulong)g.WorkingSet), g.Path })
            {
                Checked = true,
                Tag = g,
            });
        _list.EndUpdate();

        long total = groups.Sum(g => g.WorkingSet);
        _header.Text = groups.Count == 0
            ? $"С момента снимка ({baseline.TakenAt:dd.MM HH:mm}) новых программ нет. Можно просто почистить ОЗУ."
            : $"Новые программы с момента снимка ({baseline.TakenAt:dd.MM HH:mm}): {groups.Count}, занимают {Native.Mb((ulong)total)}. " +
              "Отмеченные будут закрыты, сохраните несохранённую работу.";
    }

    void SetAllChecked(bool value)
    {
        foreach (ListViewItem item in _list.Items) item.Checked = value;
    }

    void WhitelistSelected()
    {
        foreach (var item in _list.SelectedItems.Cast<ListViewItem>().ToList())
        {
            var g = (ProcessGroup)item.Tag!;
            if (!S.IsWhitelisted(g.ExeName)) S.Whitelist.Add(g.ExeName);
            _list.Items.Remove(item);
            Log.Write($"В белый список: {g.ExeName}");
        }
        S.Save();
    }

    void RevealSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        var g = (ProcessGroup)_list.SelectedItems[0].Tag!;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{g.Path}\"") { UseShellExecute = true }); }
        catch { }
    }

    async Task RunAsync()
    {
        var selected = _list.CheckedItems.Cast<ListViewItem>().Select(i => (ProcessGroup)i.Tag!).ToList();
        bool mem = _memory.Checked, explorer = _explorer.Checked, dnsTemp = _dnsTemp.Checked, background = S.PauseBackground;

        S.CleanMemory = mem;
        S.RestartExplorer = explorer;
        S.FlushDnsAndTemp = dnsTemp;
        bool cs2Changed = S.Cs2SkipCpu0 != _cs2Affinity.Checked;
        S.Cs2SkipCpu0 = _cs2Affinity.Checked;
        S.Save();
        var backgroundIds = S.BackgroundItemIds.ToList();
        if (cs2Changed) Log.Write($"CS2 без CPU 0: {(S.Cs2SkipCpu0 ? "включено" : "выключено")}");
        _app.Cs2SettingChanged();

        if (selected.Count == 0 && !mem && !explorer && !dnsTemp && !background)
        {
            _status.Text = S.Cs2SkipCpu0 ? "Сохранено: CPU 0 будет отключаться для CS2." : "Ничего не выбрано.";
            return;
        }

        SetBusy(true);
        Log.Write($"Подготовка: закрыть {selected.Count} программ ({string.Join(", ", selected.Select(g => g.ExeName))}); " +
                  $"ОЗУ={mem}, explorer={explorer}, DNS/TEMP={dnsTemp}, фон={background}");

        (string full, string brief, bool warn) result;
        try
        {
            result = await Task.Run(() => Prepare(selected, mem, explorer, dnsTemp, background ? backgroundIds : null, SetStatus));
        }
        catch (Exception ex)
        {
            Log.Write($"Подготовка упала: {ex}");
            result = ($"Ошибка: {ex.Message}", "Ошибка при подготовке, подробности в логе.", true);
        }

        SetBusy(false);
        _app.Notify(result.brief, result.warn ? ToolTipIcon.Warning : ToolTipIcon.Info);
        LoadCandidates(); // показываем, что осталось незакрытым (например, отказано в доступе)
        _status.ForeColor = result.warn ? Theme.P.Warn : Theme.P.Text;
        _status.Text = result.full;
    }

    static (string full, string brief, bool warn) Prepare(List<ProcessGroup> groups, bool mem, bool explorer, bool dnsTemp,
        List<string>? backgroundIds, Action<string> status)
    {
        var full = new StringBuilder();
        var brief = new StringBuilder();
        bool warn = false;

        if (groups.Count > 0)
        {
            int closed = ProcessKiller.Close(groups, status);
            full.AppendLine($"Закрыто процессов: {closed}.");
            brief.Append($"Закрыто процессов: {closed}. ");
        }

        if (backgroundIds != null)
        {
            status("Приостанавливаю фоновые службы Windows…");
            var (ok, errors) = BackgroundServices.Apply(backgroundIds, PauseMode.Temporary);
            full.AppendLine($"Фоновые службы приостановлены: {ok}" +
                            (errors.Count > 0 ? $", не получилось: {string.Join("; ", errors)}" : "") +
                            ". Вернуть: раздел «Фоновые службы» или меню значка.");
            brief.Append($"Фоновых служб приостановлено: {ok}. ");
            if (errors.Count > 0) warn = true;
        }

        if (dnsTemp)
        {
            status("Сбрасываю DNS и чищу TEMP…");
            bool dns = SystemTweaks.FlushDns();
            var (files, bytes) = SystemTweaks.CleanTemp();
            full.AppendLine($"DNS {(dns ? "сброшен" : "не сброшен")}, из TEMP удалено файлов: {files} ({Native.Mb(bytes)}).");
        }

        if (explorer)
        {
            status("Перезапускаю проводник…");
            bool ok = SystemTweaks.RestartExplorer();
            full.AppendLine(ok ? "Проводник перезапущен." : "Проводник перезапустить не удалось (см. лог).");
            if (!ok) warn = true;
        }

        if (mem)
        {
            status("Чищу оперативную память…");
            var r = MemoryCleaner.Clean();
            full.AppendLine($"Свободная ОЗУ: {MemoryCleaner.Fmt(r.Before?.FreeBytes)} → {MemoryCleaner.Fmt(r.After?.FreeBytes)}, " +
                            $"standby: {MemoryCleaner.Fmt(r.Before?.StandbyBytes)} → {MemoryCleaner.Fmt(r.After?.StandbyBytes)}.");
            brief.Append($"Свободно ОЗУ: {MemoryCleaner.Fmt(r.Before?.FreeBytes)} → {MemoryCleaner.Fmt(r.After?.FreeBytes)}. ");
            if (r.Errors.Count > 0)
            {
                full.AppendLine("Ошибки очистки: " + string.Join("; ", r.Errors));
                warn = true;
            }

            full.Append($"Nonpaged pool: {Native.Mb(r.NonpagedPool)}.");
            if (r.NonpagedPool > NonpagedWarnBytes)
            {
                const string leak = " Это много: похоже на утечку памяти в драйвере, её лечит только перезагрузка.";
                full.Append(leak);
                brief.Append(leak);
                warn = true;
            }
        }

        if (brief.Length == 0) brief.Append("Готово.");
        return (full.ToString().Trim(), brief.ToString().Trim(), warn);
    }

    void SetStatus(string text)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() => { _status.ForeColor = Theme.P.Muted; _status.Text = text; });
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        _progress.Visible = busy;
        foreach (var c in new Control[] { _run, _refresh, _checkAll, _checkNone, _list, _memory, _explorer, _dnsTemp, _cs2Affinity })
            c.Enabled = !busy;
        UseWaitCursor = busy;
    }
}
