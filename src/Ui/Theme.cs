using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace StaScan;

enum ThemeMode { System, Light, Dark }

/// <summary>Цвета одной темы. Все элементы интерфейса берут цвета только отсюда.</summary>
sealed record Palette(
    bool IsDark,
    Color Bg, Color Surface, Color SurfaceHover,
    Color Text, Color Muted, Color Line,
    Color Accent, Color AccentHover, Color OnAccent,
    Color Warn, Color RowOk, Color RowCaution, Color RowBad,
    Color Input, Color Button, Color ButtonHover, Color ButtonBorder)
{
    public static readonly Palette Light = new(
        IsDark: false,
        Bg: Color.White, Surface: Color.FromArgb(243, 244, 246), SurfaceHover: Color.FromArgb(229, 231, 235),
        Text: Color.FromArgb(17, 24, 39), Muted: Color.FromArgb(107, 114, 128), Line: Color.FromArgb(229, 231, 235),
        Accent: Color.FromArgb(22, 163, 74), AccentHover: Color.FromArgb(21, 128, 61), OnAccent: Color.White,
        Warn: Color.FromArgb(180, 83, 9),
        RowOk: Color.FromArgb(220, 252, 231), RowCaution: Color.FromArgb(254, 249, 195), RowBad: Color.FromArgb(254, 226, 226),
        Input: Color.White, Button: Color.White, ButtonHover: Color.FromArgb(243, 244, 246), ButtonBorder: Color.FromArgb(209, 213, 219));

    public static readonly Palette Dark = new(
        IsDark: true,
        Bg: Color.FromArgb(32, 32, 32), Surface: Color.FromArgb(24, 24, 24), SurfaceHover: Color.FromArgb(48, 48, 48),
        Text: Color.FromArgb(243, 244, 246), Muted: Color.FromArgb(156, 163, 175), Line: Color.FromArgb(58, 58, 58),
        Accent: Color.FromArgb(74, 222, 128), AccentHover: Color.FromArgb(134, 239, 172), OnAccent: Color.FromArgb(5, 46, 22),
        Warn: Color.FromArgb(251, 191, 36),
        RowOk: Color.FromArgb(20, 70, 40), RowCaution: Color.FromArgb(75, 62, 12), RowBad: Color.FromArgb(90, 28, 28),
        Input: Color.FromArgb(40, 40, 40), Button: Color.FromArgb(45, 45, 45), ButtonHover: Color.FromArgb(60, 60, 60), ButtonBorder: Color.FromArgb(80, 80, 80));
}

static class Theme
{
    public static readonly Font Base = new("Segoe UI", 9.5f);
    public static readonly Font Small = new("Segoe UI", 8.5f);
    public static readonly Font Bold = new("Segoe UI Semibold", 9.5f);
    public static readonly Font Title = new("Segoe UI Semibold", 15f);
    public static readonly Font Group = new("Segoe UI Semibold", 10.5f);
    public static readonly Font Mono = new("Consolas", 10f);

    public static Palette P { get; private set; } = Palette.Light;
    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    public static void Set(ThemeMode mode)
    {
        Mode = mode;
        P = mode switch
        {
            ThemeMode.Dark => Palette.Dark,
            ThemeMode.Light => Palette.Light,
            _ => SystemIsDark() ? Palette.Dark : Palette.Light,
        };
    }

    /// <summary>Тёмная ли тема приложений в Параметрах Windows.</summary>
    public static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    // ── Готовые элементы ──────────────────────────────────────────────────────────

    public static Label PageTitle(string text) => new()
    {
        Text = text,
        Font = Title,
        Dock = DockStyle.Top,
        Height = 48,
        Padding = new Padding(20, 12, 20, 0),
    };

    public static Label PageHint(string text, int height = 40) => new()
    {
        Text = text,
        ForeColor = P.Muted,
        Dock = DockStyle.Top,
        Height = height,
        Padding = new Padding(20, 0, 20, 0),
    };

    public static Label Section(string text) => new()
    {
        Text = text,
        Font = Group,
        AutoSize = true,
        Margin = new Padding(0, 14, 0, 6),
    };

    public static UiButton Primary(string text) => new(text, ButtonKind.Primary);
    public static UiButton Secondary(string text) => new(text, ButtonKind.Secondary);

    public static CheckBox Check(string text, bool value) => new()
    {
        Text = text,
        Checked = value,
        AutoSize = true,
        Margin = new Padding(0, 3, 0, 3),
    };

    /// <summary>Тулбар с кнопками: одинаковые отступы во всех разделах.</summary>
    public static FlowLayoutPanel Toolbar(params Control[] controls)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(16, 4, 16, 8), WrapContents = true };
        foreach (var c in controls) c.Margin = new Padding(4, 0, 4, 0);
        bar.Controls.AddRange(controls);
        return bar;
    }

    public static ListView List(bool checkBoxes = false)
    {
        var lv = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            CheckBoxes = checkBoxes,
            BorderStyle = BorderStyle.None,
            ShowItemToolTips = true,
            OwnerDraw = true,
        };
        // свой заголовок: стандартный всегда светлый
        lv.DrawColumnHeader += (_, e) =>
        {
            using var bg = new SolidBrush(P.Surface);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var line = new Pen(P.Line);
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.LeftAndRightPadding |
                        (e.Header?.TextAlign switch
                        {
                            HorizontalAlignment.Right => TextFormatFlags.Right,
                            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
                            _ => TextFormatFlags.Left,
                        });
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, Bold, e.Bounds, P.Muted, flags);
        };
        lv.DrawItem += (_, e) => e.DrawDefault = true;
        lv.DrawSubItem += (_, e) => e.DrawDefault = true;

        // последняя колонка растягивается до края, чтобы справа не оставался «пустой» светлый кусок заголовка
        bool fitting = false;
        void Fit()
        {
            if (fitting || lv.Columns.Count == 0 || !lv.IsHandleCreated) return;
            fitting = true;
            int used = 0;
            for (int i = 0; i < lv.Columns.Count - 1; i++) used += lv.Columns[i].Width;
            int w = lv.ClientSize.Width - used;
            if (w > 60) lv.Columns[^1].Width = w;
            fitting = false;
        }
        lv.SizeChanged += (_, _) => Fit();
        lv.HandleCreated += (_, _) => Fit();
        lv.ColumnWidthChanged += (_, e) => { if (e.ColumnIndex < lv.Columns.Count - 1) Fit(); };
        return lv;
    }

    // ── Применение темы ───────────────────────────────────────────────────────────

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    /// <summary>Тёмный заголовок окна (Windows 10 20H1+ / 11).</summary>
    public static void ApplyTitleBar(Form f)
    {
        void Apply()
        {
            int on = P.IsDark ? 1 : 0;
            DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int));
        }
        if (f.IsHandleCreated) Apply(); else f.HandleCreated += (_, _) => Apply();
    }

    /// <summary>
    /// Проходит по дереву элементов и красит стандартные контролы, которые не берут цвета родителя сами
    /// (поля ввода, списки, галки, полосы прокрутки).
    /// </summary>
    public static void Style(Control root)
    {
        switch (root)
        {
            case ToggleSwitch or UiButton:
                break;
            case ListView lv:
                lv.BackColor = P.Bg;
                lv.ForeColor = P.Text;
                DarkScrollbars(lv);
                break;
            case TextBox tb:
                tb.BackColor = P.Input;
                tb.ForeColor = P.Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
                break;
            case LinkLabel ll:
                ll.LinkColor = ll.ActiveLinkColor = ll.VisitedLinkColor = P.Accent;
                break;
            case CheckBox cb:
                cb.FlatStyle = FlatStyle.Flat;
                cb.FlatAppearance.BorderColor = P.ButtonBorder;
                cb.FlatAppearance.CheckedBackColor = P.Bg;
                cb.FlatAppearance.MouseOverBackColor = P.Bg;
                cb.ForeColor = P.Text;
                break;
            case RadioButton rb:
                rb.FlatStyle = FlatStyle.Flat;
                rb.FlatAppearance.BorderColor = P.ButtonBorder;
                rb.ForeColor = P.Text;
                break;
            case ScrollableControl sc when sc.AutoScroll:
                DarkScrollbars(sc);
                break;
        }
        foreach (Control c in root.Controls) Style(c);
    }

    static void DarkScrollbars(Control c)
    {
        void Apply() => SetWindowTheme(c.Handle, P.IsDark ? "DarkMode_Explorer" : "Explorer", null);
        if (c.IsHandleCreated) Apply(); else c.HandleCreated += (_, _) => Apply();
    }

    public static ToolStripRenderer MenuRenderer() => new MenuRendererImpl();

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    sealed class MenuRendererImpl : ToolStripProfessionalRenderer
    {
        public MenuRendererImpl() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? P.Muted : e.Item is ToolStripLabel ? P.Muted : P.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using var pen = new Pen(P.Accent, 2f);
            g.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.55f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.75f),
                new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.3f),
            });
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = P.Text;
            base.OnRenderArrow(e);
        }
    }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => P.Bg;
        public override Color ImageMarginGradientBegin => P.Bg;
        public override Color ImageMarginGradientMiddle => P.Bg;
        public override Color ImageMarginGradientEnd => P.Bg;
        public override Color MenuBorder => P.ButtonBorder;
        public override Color MenuItemBorder => P.SurfaceHover;
        public override Color MenuItemSelected => P.SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => P.SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => P.SurfaceHover;
        public override Color MenuItemPressedGradientBegin => P.SurfaceHover;
        public override Color MenuItemPressedGradientEnd => P.SurfaceHover;
        public override Color SeparatorDark => P.Line;
        public override Color SeparatorLight => P.Line;
        public override Color CheckBackground => P.Bg;
        public override Color CheckSelectedBackground => P.SurfaceHover;
        public override Color CheckPressedBackground => P.SurfaceHover;
    }
}

enum ButtonKind { Primary, Secondary, Nav, Icon }

/// <summary>Единая кнопка приложения: скруглённая, одной высоты, цвета из темы.</summary>
sealed class UiButton : Button
{
    public ButtonKind Kind { get; }
    bool _hover, _down, _selected;

    public UiButton(string text, ButtonKind kind)
    {
        Kind = kind;
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Font = kind == ButtonKind.Primary ? Theme.Bold : Theme.Base;
        Cursor = Cursors.Hand;
        AutoSize = kind is ButtonKind.Primary or ButtonKind.Secondary;
        Height = kind == ButtonKind.Nav ? 40 : 34;
        if (kind == ButtonKind.Icon) Size = new Size(34, 30);
        if (kind == ButtonKind.Nav) TabStop = false;
    }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Font = value ? Theme.Bold : Theme.Base; Invalidate(); }
    }

    public override Size GetPreferredSize(Size proposed)
    {
        var t = TextRenderer.MeasureText(Text, Font);
        return new Size(t.Width + 32, 34);
    }

    public override void NotifyDefault(bool value) { }
    protected override bool ShowFocusCues => Kind != ButtonKind.Nav && base.ShowFocusCues;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var P = Theme.P;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var parentBg = Parent?.BackColor ?? P.Bg;
        g.Clear(parentBg);

        Color fill, text, border = Color.Empty;
        switch (Kind)
        {
            case ButtonKind.Primary:
                fill = !Enabled ? P.ButtonBorder : _down || _hover ? P.AccentHover : P.Accent;
                text = !Enabled ? P.Muted : P.OnAccent;
                break;
            case ButtonKind.Nav:
                fill = _selected ? P.Bg : _hover ? P.SurfaceHover : parentBg;
                text = P.Text;
                break;
            case ButtonKind.Icon:
                fill = _hover ? P.SurfaceHover : parentBg;
                text = Enabled ? P.Text : P.Muted;
                break;
            default:
                fill = !Enabled ? parentBg : _down || _hover ? P.ButtonHover : P.Button;
                text = Enabled ? P.Text : P.Muted;
                border = P.ButtonBorder;
                break;
        }

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.Rounded(r, 6))
        {
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
            if (border != Color.Empty)
            {
                using var pen = new Pen(border);
                g.DrawPath(pen, path);
            }
        }

        if (Kind == ButtonKind.Nav && _selected)
        {
            using var bar = new SolidBrush(P.Accent);
            g.FillRectangle(bar, 0, 10, 3, Height - 20);
        }

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    (Kind == ButtonKind.Nav ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter);
        var textRect = Kind == ButtonKind.Nav ? new Rectangle(16, 0, Width - 20, Height) : ClientRectangle;
        TextRenderer.DrawText(g, Text, Font, textRect, text, flags);

        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -3, -3), text, fill);
    }
}

/// <summary>Переключатель в стиле Windows 11. Сам не переключается: состояние ставит владелец после выполнения действия.</summary>
sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        AutoCheck = false;
        AutoSize = false;
        Size = new Size(44, 22);
        Cursor = Cursors.Hand;
        Text = "";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var P = Theme.P;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? P.Bg);

        var track = new RectangleF(1, 1, Width - 3, Height - 3);
        using var path = Theme.Rounded(track, track.Height / 2);
        var off = Enabled ? P.Muted : P.ButtonBorder;
        if (Checked)
        {
            using var fill = new SolidBrush(Enabled ? P.Accent : P.ButtonBorder);
            g.FillPath(fill, path);
        }
        else
        {
            using var fill = new SolidBrush(P.Bg);
            g.FillPath(fill, path);
            using var pen = new Pen(off, 1.2f);
            g.DrawPath(pen, path);
        }

        float knob = track.Height - 8;
        float x = Checked ? track.Right - knob - 4 : track.X + 4;
        using var knobBrush = new SolidBrush(Checked ? P.OnAccent : off);
        g.FillEllipse(knobBrush, x, track.Y + 4, knob, knob);

        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, ClientRectangle);
    }
}
