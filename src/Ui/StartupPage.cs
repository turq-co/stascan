namespace StaScan;

/// <summary>"Startup apps" section: what starts with Windows, with a check box to turn each item off.</summary>
sealed class StartupPage : UserControl, IPage
{
    readonly ListView _list;
    readonly Label _status;
    bool _loading;

    public bool IsBusy => false;

    public StartupPage()
    {
        var title = Theme.PageTitle(L.T("Startup apps"));
        var hint = Theme.PageHint(
            L.T("Programs that start with Windows keep running in the background all session. Uncheck the ones you do not need. This works exactly like Task Manager → Startup apps, so items can be turned back on from either place."), 40);

        _list = Theme.List(checkBoxes: true);
        _list.Columns.Add(L.T("Program"), 220);
        _list.Columns.Add(L.T("Location"), 200);
        _list.Columns.Add(L.T("Command"), 400);
        _list.ItemChecked += OnItemChecked;
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 0) };
        host.Controls.Add(_list);

        var refresh = Theme.Secondary(L.T("Refresh"));
        refresh.Click += (_, _) => Reload();
        var toolbar = Theme.Toolbar(refresh);
        toolbar.Dock = DockStyle.Bottom;
        toolbar.Padding = new Padding(16, 8, 16, 0);
        _status = new Label { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(20, 8, 20, 0), ForeColor = Theme.P.Muted };

        Controls.Add(host);
        Controls.Add(toolbar);
        Controls.Add(_status);
        Controls.Add(hint);
        Controls.Add(title);
    }

    public void OnShown() => Reload();

    void Reload()
    {
        _loading = true;
        try
        {
            var apps = StartupApps.List();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var app in apps)
                _list.Items.Add(new ListViewItem(new[] { app.Name, L.T(app.Location), app.Command })
                {
                    Checked = app.Enabled,
                    Tag = app,
                    ToolTipText = app.Command,
                    ForeColor = app.Enabled ? Theme.P.Text : Theme.P.Muted,
                });
            _list.EndUpdate();
            int on = apps.Count(a => a.Enabled);
            _status.ForeColor = Theme.P.Muted;
            _status.Text = L.F("{0} startup items, {1} enabled.", apps.Count, on);
        }
        catch (Exception ex)
        {
            _status.Text = L.F("Could not read startup apps: {0}", ex.Message);
            Log.Write($"Startup apps: {ex}");
        }
        finally
        {
            _loading = false;
        }
    }

    void OnItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (_loading || e.Item.Tag is not StartupApp app) return;
        try
        {
            StartupApps.SetEnabled(app, e.Item.Checked);
            e.Item.ForeColor = e.Item.Checked ? Theme.P.Text : Theme.P.Muted;
            e.Item.Tag = app with { Enabled = e.Item.Checked };
            _status.ForeColor = Theme.P.Muted;
            _status.Text = L.F(e.Item.Checked ? "{0}: will start with Windows (takes effect at next sign-in)." : "{0}: will not start with Windows (takes effect at next sign-in).", app.Name);
        }
        catch (Exception ex)
        {
            _status.ForeColor = Theme.P.Warn;
            _status.Text = L.F("Could not change {0}: {1}", app.Name, ex.Message);
            _loading = true;
            e.Item.Checked = !e.Item.Checked;
            _loading = false;
        }
    }
}
