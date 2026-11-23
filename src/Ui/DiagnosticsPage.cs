using System.Drawing.Drawing2D;
using System.Globalization;

namespace StaScan;

/// <summary>"Diagnostics" section: memory trends, kernel pool by driver tag, video memory by process.</summary>
sealed class DiagnosticsPage : UserControl, IPage
{
    const double LeakWarnMbPerHour = 20;

    readonly AppServices _app;
    readonly Label _stats, _leak, _status;
    readonly MiniChart _poolChart, _ramChart;
    readonly ListView _pool, _gpu;
    readonly UiButton _refresh, _findDriver;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    bool _refreshing, _searching;

    public bool IsBusy => false;

    public DiagnosticsPage(AppServices app)
    {
        _app = app;

        var title = Theme.PageTitle("Diagnostics");
        var hint = Theme.PageHint(
            "Shows what is using memory right now and how it changed since StaScan started. " +
            "A kernel pool that keeps growing points to a leaking driver, which only a reboot or a driver update fixes.", 40);

        _stats = new Label { Dock = DockStyle.Top, Height = 26, Padding = new Padding(20, 4, 20, 0), Font = Theme.Bold };
        _leak = new Label { Dock = DockStyle.Top, Height = 0, Padding = new Padding(20, 2, 20, 0), ForeColor = Theme.P.Warn };

        _poolChart = new MiniChart("Nonpaged pool, last 24 h") { Dock = DockStyle.Fill };
        _ramChart = new MiniChart("Available RAM, last 24 h") { Dock = DockStyle.Fill };
        var charts = new TableLayoutPanel { Dock = DockStyle.Top, Height = 140, ColumnCount = 2, Padding = new Padding(16, 6, 16, 0) };
        charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        charts.Controls.Add(_poolChart, 0, 0);
        charts.Controls.Add(_ramChart, 1, 0);

        _pool = Theme.List();
        _pool.Columns.Add("Tag", 70);
        _pool.Columns.Add("Nonpaged", 90, HorizontalAlignment.Right);
        _pool.Columns.Add("Growth", 90, HorizontalAlignment.Right);
        _pool.Columns.Add("Driver", 140);
        _pool.MultiSelect = false;
        _gpu = Theme.List();
        _gpu.Columns.Add("Process", 200);
        _gpu.Columns.Add("Video memory", 110, HorizontalAlignment.Right);

        var lists = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(20, 4, 20, 0) };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        lists.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lists.Controls.Add(new Label { Text = "Kernel pool by driver tag", Font = Theme.Group, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 0);
        lists.Controls.Add(new Label { Text = "Video memory by process", Font = Theme.Group, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(12, 0, 0, 0) }, 1, 0);
        var poolHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 6, 0) };
        poolHost.Controls.Add(_pool);
        var gpuHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 4, 0, 0) };
        gpuHost.Controls.Add(_gpu);
        lists.Controls.Add(poolHost, 0, 1);
        lists.Controls.Add(gpuHost, 1, 1);

        _refresh = Theme.Secondary("Refresh");
        _refresh.Click += async (_, _) => await RefreshAsync();
        _findDriver = Theme.Secondary("Find driver for selected tag");
        _findDriver.Click += async (_, _) => await FindDriverAsync();
        var toolbar = Theme.Toolbar(_refresh, _findDriver);
        toolbar.Dock = DockStyle.Bottom;
        toolbar.Padding = new Padding(16, 8, 16, 0);
        _status = new Label { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(20, 8, 20, 0), ForeColor = Theme.P.Muted };

        Controls.Add(lists);
        Controls.Add(toolbar);
        Controls.Add(_status);
        Controls.Add(charts);
        Controls.Add(_leak);
        Controls.Add(_stats);
        Controls.Add(hint);
        Controls.Add(title);

        _timer.Tick += async (_, _) => { if (Visible) await RefreshAsync(); };
        VisibleChanged += (_, _) => _timer.Enabled = Visible;
        Disposed += (_, _) => _timer.Dispose();
    }

    public void OnShown()
    {
        _timer.Enabled = true;
        _ = RefreshAsync();
    }

    async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var metrics = _app.Metrics;
            var data = await Task.Run(() =>
            {
                var now = MetricsHistory.Now();
                var perf = Native.GetPerf();
                var tags = PoolTags.Query();
                var gpu = GpuMemory.Query();
                return (now, perf, tags, gpu, history: metrics.Snapshot(), growth: metrics.NonpagedGrowthMbPerHour());
            });
            if (IsDisposed) return;

            if (data.now is { } s && data.perf is { } p)
                _stats.Text = $"Available RAM {Native.Gb(s.Free + s.Standby)} (standby {Native.Gb(s.Standby)})   ·   " +
                              $"Nonpaged pool {Native.Mb(s.Nonpaged)}   ·   Commit {Native.Gb(p.CommitBytes)} of {Native.Gb(p.CommitLimitBytes)}";

            var history = data.history.ToList();
            if (data.now is { } live) history.Add(live);
            // the background sampler may have stored a point after the live one was read: keep time order
            history = history.OrderBy(h => h.At).ToList();
            _poolChart.SetData(history.Select(h => (h.At, h.Nonpaged / 1048576.0)).ToList(), "MB");
            _ramChart.SetData(history.Select(h => (h.At, (h.Free + h.Standby) / 1073741824.0)).ToList(), "GB");

            FillPool(data.tags);
            FillGpu(data.gpu);

            if (data.growth is { } g && g > LeakWarnMbPerHour)
            {
                var top = TopGrowth(data.tags).FirstOrDefault();
                _leak.Height = 24;
                _leak.Text = $"! Nonpaged pool is growing by {g:0} MB per hour" +
                             (top.Tag != null ? $", mostly tag \"{top.Tag}\". Select it and click \"Find driver\"." : ".");
            }
            else
            {
                _leak.Height = 0;
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Could not read diagnostics: " + ex.Message;
            Log.Write($"Diagnostics refresh failed: {ex}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    IEnumerable<(string Tag, long Growth)> TopGrowth(List<PoolTags.Entry> tags) =>
        tags.Select(t => (t.Tag, Growth: (long)t.NonpagedBytes - (long)_app.Metrics.PoolAtStart.GetValueOrDefault(t.Tag)))
            .Where(t => t.Growth > 0)
            .OrderByDescending(t => t.Growth);

    void FillPool(List<PoolTags.Entry> tags)
    {
        var selected = _pool.SelectedItems.Count > 0 ? _pool.SelectedItems[0].Text : null;
        var drivers = _pool.Items.Cast<ListViewItem>().ToDictionary(i => i.Text, i => i.SubItems[3].Text);
        _pool.BeginUpdate();
        _pool.Items.Clear();
        foreach (var t in tags.OrderByDescending(t => t.NonpagedBytes).Take(25))
        {
            long growth = (long)t.NonpagedBytes - (long)_app.Metrics.PoolAtStart.GetValueOrDefault(t.Tag);
            var item = new ListViewItem(new[]
            {
                t.Tag,
                Native.Mb(t.NonpagedBytes),
                growth > 1048576 ? "+" + Native.Mb((ulong)growth) : "",
                drivers.GetValueOrDefault(t.Tag, ""),
            });
            if (growth > 64L * 1048576) item.ForeColor = Theme.P.Warn;
            _pool.Items.Add(item);
            if (t.Tag == selected) item.Selected = true;
        }
        _pool.EndUpdate();
    }

    void FillGpu(List<GpuMemory.Entry> gpu)
    {
        _gpu.BeginUpdate();
        _gpu.Items.Clear();
        foreach (var g in gpu.Where(g => g.DedicatedBytes >= 1048576).Take(25))
            _gpu.Items.Add(new ListViewItem(new[] { g.Name, Native.Mb(g.DedicatedBytes) }));
        if (gpu.Count == 0)
            _gpu.Items.Add(new ListViewItem(new[] { "No data from GPU counters", "" }) { ForeColor = Theme.P.Muted });
        _gpu.EndUpdate();
    }

    async Task FindDriverAsync()
    {
        if (_searching) return;
        if (_pool.SelectedItems.Count == 0)
        {
            _status.Text = "Select a tag in the kernel pool list first.";
            return;
        }
        var item = _pool.SelectedItems[0];
        var tag = item.Text;
        _searching = true;
        _findDriver.Enabled = false;
        _status.Text = $"Searching loaded drivers for tag \"{tag}\"…";
        try
        {
            var hits = await Task.Run(() => PoolTags.FindDrivers(tag.Trim()));
            var names = hits.Select(Path.GetFileName).ToList();
            item.SubItems[3].Text = names.Count == 0 ? "not found" : string.Join(", ", names);
            _status.Text = names.Count == 0
                ? $"No loaded driver contains \"{tag}\". It is probably allocated by the Windows kernel itself."
                : $"\"{tag}\" is used by: {string.Join(", ", hits)}";
            Log.Write($"Driver search for {tag}: {(hits.Count == 0 ? "none" : string.Join(", ", hits))}");
        }
        finally
        {
            _searching = false;
            _findDriver.Enabled = true;
        }
    }
}

/// <summary>A small line chart drawn with GDI+ (no charting library needed).</summary>
sealed class MiniChart : Control
{
    readonly string _title;
    List<(DateTime at, double value)> _data = new();
    string _unit = "";

    public MiniChart(string title)
    {
        _title = title;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Margin = new Padding(4);
    }

    public void SetData(List<(DateTime at, double value)> data, string unit)
    {
        _data = data;
        _unit = unit;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var P = Theme.P;
        var g = e.Graphics;
        g.Clear(P.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var box = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Rounded(box, 6))
        using (var fill = new SolidBrush(P.Surface))
        using (var border = new Pen(P.Line))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        TextRenderer.DrawText(g, _title, Theme.Small, new Point(10, 6), P.Muted);
        if (_data.Count < 2)
        {
            TextRenderer.DrawText(g, "Collecting data… (one point per minute)", Theme.Small, new Point(10, Height / 2 - 6), P.Muted);
            return;
        }

        // a minimum vertical range, so a flat line with tiny noise does not look like a steep climb
        double min = _data.Min(d => d.value), max = _data.Max(d => d.value);
        double minRange = _unit == "GB" ? 1 : 50;
        if (max - min < minRange)
        {
            double mid = (max + min) / 2;
            min = Math.Max(0, mid - minRange / 2);
            max = min + minRange;
        }
        string F(double v) => v.ToString(_unit == "GB" ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + _unit;

        var current = _data[^1].value;
        TextRenderer.DrawText(g, F(current), Theme.Bold, new Point(Width - 10 - TextRenderer.MeasureText(F(current), Theme.Bold).Width, 4), P.Text);

        var plot = new RectangleF(10, 28, Width - 20, Height - 48);
        var t0 = _data[0].at;
        double span = Math.Max(1, (_data[^1].at - t0).TotalSeconds);
        var pts = _data.Select(d => new PointF(
            plot.Left + (float)((d.at - t0).TotalSeconds / span) * plot.Width,
            plot.Bottom - (float)((d.value - min) / (max - min)) * plot.Height)).ToArray();
        using (var pen = new Pen(P.Accent, 2f))
            g.DrawLines(pen, pts);

        TextRenderer.DrawText(g, $"{F(min)} – {F(max)}  ·  since {t0.ToString("HH:mm", CultureInfo.InvariantCulture)}",
            Theme.Small, new Point(10, Height - 20), P.Muted);
    }
}
