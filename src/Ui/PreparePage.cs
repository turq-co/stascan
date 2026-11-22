using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace StaScan;

/// <summary>"Game prep" section: programs started since the snapshot plus extra actions.</summary>
sealed class PreparePage : UserControl, IPage
{
    const ulong NonpagedWarnBytes = 1536UL * 1024 * 1024; // 1.5 GB: above this a driver leak is likely

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

        var title = Theme.PageTitle("Game prep");
        _header = Theme.PageHint("", 44);

        _list = Theme.List(checkBoxes: true);
        _list.Columns.Add("Program", 220);
        _list.Columns.Add("Processes", 90, HorizontalAlignment.Right);
        _list.Columns.Add("RAM", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Path", 340);
        var itemMenu = new ContextMenuStrip { Renderer = Theme.MenuRenderer() };
        itemMenu.Items.Add("Add to whitelist (never offer again)", null, (_, _) => WhitelistSelected());
        itemMenu.Items.Add("Open file location", null, (_, _) => RevealSelected());
        _list.ContextMenuStrip = itemMenu;
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 0) };
        listHost.Controls.Add(_list);

        _checkAll = Theme.Secondary("Select all");
        _checkNone = Theme.Secondary("Select none");
        _refresh = Theme.Secondary("Refresh list");
        var usb = Theme.Secondary("Check USB ports");
        _checkAll.Click += (_, _) => SetAllChecked(true);
        _checkNone.Click += (_, _) => SetAllChecked(false);
        _refresh.Click += (_, _) => LoadCandidates();
        usb.Click += (_, _) => openUsb();
        var listButtons = Theme.Toolbar(_checkAll, _checkNone, _refresh, usb);
        listButtons.Dock = DockStyle.Bottom;
        listButtons.Padding = new Padding(16, 8, 16, 0);

        _memory = Theme.Check("Free up RAM (working sets, standby list, file cache)", S.CleanMemory);
        _explorer = Theme.Check("Restart File Explorer (explorer.exe)", S.RestartExplorer);
        _dnsTemp = Theme.Check("Flush DNS cache and delete old files from %TEMP%", S.FlushDnsAndTemp);
        _cs2Affinity = Theme.Check("Exclude CPU 0 for CS2 (also applies if the game is launched later)", S.Cs2SkipCpu0);
        _bgNote = new Label { AutoSize = true, ForeColor = Theme.P.Muted, Margin = new Padding(0, 6, 0, 0) };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(20, 0, 20, 4),
        };
        actions.Controls.AddRange(new Control[] { Theme.Section("Also"), _memory, _explorer, _dnsTemp, _cs2Affinity, _bgNote });

        _status = new Label { Dock = DockStyle.Fill, Padding = new Padding(20, 8, 20, 0) };
        _progress = new ProgressBar { Dock = DockStyle.Top, Height = 4, Style = ProgressBarStyle.Marquee, Visible = false };
        _run = Theme.Primary("Prepare for gaming");
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
            ? $"Background services marked in the Background services section ({n}) will also be paused."
            : "Background services are left alone (this can be turned on in the Background services section).";
        LoadCandidates();
    }

    void LoadCandidates()
    {
        var baseline = _app.GetBaseline();
        var groups = ProcessScanner.FindCandidates(baseline, S);
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var g in groups)
            _list.Items.Add(new ListViewItem(new[] { g.ExeName, g.Pids.Count.ToString(CultureInfo.InvariantCulture), Native.Mb((ulong)g.WorkingSet), g.Path })
            {
                Checked = true,
                Tag = g,
            });
        _list.EndUpdate();

        long total = groups.Sum(g => g.WorkingSet);
        var taken = baseline.TakenAt.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);
        _header.Text = groups.Count == 0
            ? $"No new programs since the snapshot ({taken}). You can still free up RAM."
            : $"Programs started since the snapshot ({taken}): {groups.Count}, using {Native.Mb((ulong)total)}. " +
              "Checked programs will be closed, so save your work first.";
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
            Log.Write($"Added to whitelist: {g.ExeName}");
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
        if (cs2Changed) Log.Write($"CS2 without CPU 0: {(S.Cs2SkipCpu0 ? "on" : "off")}");
        _app.Cs2SettingChanged();

        if (selected.Count == 0 && !mem && !explorer && !dnsTemp && !background)
        {
            _status.Text = S.Cs2SkipCpu0 ? "Saved: CPU 0 will be excluded for CS2." : "Nothing selected.";
            return;
        }

        SetBusy(true);
        Log.Write($"Game prep: close {selected.Count} programs ({string.Join(", ", selected.Select(g => g.ExeName))}); " +
                  $"RAM={mem}, explorer={explorer}, DNS/TEMP={dnsTemp}, background={background}");

        (string full, string brief, bool warn) result;
        try
        {
            result = await Task.Run(() => Prepare(selected, mem, explorer, dnsTemp, background ? backgroundIds : null, SetStatus));
        }
        catch (Exception ex)
        {
            Log.Write($"Game prep failed: {ex}");
            result = ($"Error: {ex.Message}", "Game prep failed, see the log for details.", true);
        }

        SetBusy(false);
        _app.Notify(result.brief, result.warn ? ToolTipIcon.Warning : ToolTipIcon.Info);
        LoadCandidates(); // show anything that is still running (for example, access denied)
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
            full.AppendLine($"Processes closed: {closed}.");
            brief.Append($"Processes closed: {closed}. ");
        }

        if (backgroundIds != null)
        {
            status("Pausing Windows background services…");
            var (ok, errors) = BackgroundServices.Apply(backgroundIds, PauseMode.Temporary);
            full.AppendLine($"Background services paused: {ok}" +
                            (errors.Count > 0 ? $", failed: {string.Join("; ", errors)}" : "") +
                            ". To restore them, use the Background services section or the tray menu.");
            brief.Append($"Background services paused: {ok}. ");
            if (errors.Count > 0) warn = true;
        }

        if (dnsTemp)
        {
            status("Flushing DNS and cleaning TEMP…");
            bool dns = SystemTweaks.FlushDns();
            var (files, bytes) = SystemTweaks.CleanTemp();
            full.AppendLine($"DNS cache {(dns ? "flushed" : "not flushed")}, files deleted from TEMP: {files} ({Native.Mb(bytes)}).");
        }

        if (explorer)
        {
            status("Restarting File Explorer…");
            bool ok = SystemTweaks.RestartExplorer();
            full.AppendLine(ok ? "File Explorer restarted." : "Could not restart File Explorer (see the log).");
            if (!ok) warn = true;
        }

        if (mem)
        {
            status("Freeing up RAM…");
            var r = MemoryCleaner.Clean();
            full.AppendLine($"Free RAM: {MemoryCleaner.Fmt(r.Before?.FreeBytes)} → {MemoryCleaner.Fmt(r.After?.FreeBytes)}, " +
                            $"standby: {MemoryCleaner.Fmt(r.Before?.StandbyBytes)} → {MemoryCleaner.Fmt(r.After?.StandbyBytes)}.");
            brief.Append($"Free RAM: {MemoryCleaner.Fmt(r.Before?.FreeBytes)} → {MemoryCleaner.Fmt(r.After?.FreeBytes)}. ");
            if (r.Errors.Count > 0)
            {
                full.AppendLine("Memory cleanup errors: " + string.Join("; ", r.Errors));
                warn = true;
            }

            full.Append($"Nonpaged pool: {Native.Mb(r.NonpagedPool)}.");
            if (r.NonpagedPool > NonpagedWarnBytes)
            {
                const string leak = " That is high: a driver is probably leaking memory, and only a reboot will reclaim it.";
                full.Append(leak);
                brief.Append(leak);
                warn = true;
            }
        }

        if (brief.Length == 0) brief.Append("Done.");
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
