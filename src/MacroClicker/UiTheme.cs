using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MacroClicker;

/// <summary>按钮语义变体。</summary>
internal enum AppVariant { Primary, Success, Danger, Neutral, Ghost }

/// <summary>主题色板：深色与浅色两套完整配色。</summary>
internal sealed class ThemeColors
{
    public Color Window, Panel, Card, Field, Border, Divider, Text, SubText;
    public Color Accent, AccentHover, AccentPress, OnAccent;
    public Color Success, SuccessHover, SuccessPress, OnSuccess;
    public Color Danger, DangerHover, DangerPress, OnDanger;
    public Color Warning, RowHover, Selection, SelectionText;
}

/// <summary>全局主题：色板、自绘控件与统一样式入口。</summary>
internal static class UiTheme
{
    public static readonly Font BaseFont = new("Microsoft YaHei UI", 9F);
    public static readonly Font TitleFont = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    public static readonly Font ButtonFont = new("Microsoft YaHei UI", 9.75F);

    public static bool Dark { get; private set; } = true;

    public static readonly ThemeColors DarkC = new()
    {
        Window = Col("#17181D"), Panel = Col("#1C1E25"), Card = Col("#22242D"),
        Field = Col("#2A2D38"), Border = Col("#333743"), Divider = Col("#2A2D36"),
        Text = Col("#E9EAEE"), SubText = Col("#98A0AE"),
        Accent = Col("#5B8CFF"), AccentHover = Col("#7AA2FF"), AccentPress = Col("#4877E8"), OnAccent = Col("#FFFFFF"),
        Success = Col("#34C98B"), SuccessHover = Col("#4BD89D"), SuccessPress = Col("#29B177"), OnSuccess = Col("#07281A"),
        Danger = Col("#FF5C6C"), DangerHover = Col("#FF7A87"), DangerPress = Col("#E8495A"), OnDanger = Col("#2B070C"),
        Warning = Col("#FFB454"), RowHover = Col("#2B2F3A"), Selection = Col("#2E3D5E"), SelectionText = Col("#FFFFFF")
    };

    public static readonly ThemeColors LightC = new()
    {
        Window = Col("#F3F5F9"), Panel = Col("#FFFFFF"), Card = Col("#FFFFFF"),
        Field = Col("#FBFCFE"), Border = Col("#D9DFEA"), Divider = Col("#E8EBF1"),
        Text = Col("#1B1E27"), SubText = Col("#6B7280"),
        Accent = Col("#4F6BFF"), AccentHover = Col("#6C84FF"), AccentPress = Col("#3D55E0"), OnAccent = Col("#FFFFFF"),
        Success = Col("#16A34A"), SuccessHover = Col("#22B45A"), SuccessPress = Col("#0E8A3C"), OnSuccess = Col("#FFFFFF"),
        Danger = Col("#E5484D"), DangerHover = Col("#F06166"), DangerPress = Col("#C93A3F"), OnDanger = Col("#FFFFFF"),
        Warning = Col("#D97706"), RowHover = Col("#EEF1F8"), Selection = Col("#DCE4FF"), SelectionText = Col("#1B1E27")
    };

    public static ThemeColors C => Dark ? DarkC : LightC;

    /// <summary>主题切换后触发，订阅方自行刷新。</summary>
    public static event Action? Changed;

    private static readonly AppColorTable ColorTable = new();
    public static ToolStripProfessionalRenderer Renderer { get; } = new(ColorTable) { RoundedEdges = false };

    public static void SetDark(bool dark)
    {
        if (Dark == dark) return;
        Dark = dark;
        Changed?.Invoke();
    }

    /// <summary>递归把主题应用到控件树（自绘控件实时取色，无需处理）。</summary>
    public static void Apply(Control root)
    {
        ApplyTo(root);
        foreach (Control child in GetAll(root)) ApplyTo(child);
        root.Invalidate(true);
    }

    private static IEnumerable<Control> GetAll(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var sub in GetAll(c)) yield return sub;
        }
    }

    private static void ApplyTo(Control c)
    {
        switch (c)
        {
            case AppListView lv:
                StyleList(lv);
                break;
            case FieldWrap w:
                w.BackColor = C.Field;
                break;
            case TextBoxBase tb:
                tb.BorderStyle = BorderStyle.FixedSingle;
                tb.BackColor = C.Field;
                tb.ForeColor = C.Text;
                break;
            case NumericUpDown num:
                num.BorderStyle = BorderStyle.FixedSingle;
                num.BackColor = C.Field;
                num.ForeColor = C.Text;
                break;
            case ComboBox cmb:
                StyleCombo(cmb);
                break;
            case Label lbl:
                lbl.ForeColor = lbl.Tag as string == "sub" ? C.SubText : C.Text;
                break;
            case CheckBox ck:
                ck.ForeColor = C.Text;
                break;
            case StatusStrip ss:
                ss.BackColor = C.Panel;
                ss.Renderer = Renderer;
                ss.SizingGrip = false;
                break;
            case Form f:
                f.BackColor = C.Window;
                ApplyTitleBar(f);
                break;
        }
    }

    /// <summary>ListView 深浅色适配 + 自绘表头与单元格（悬停/选中高亮）。</summary>
    public static void StyleList(AppListView lv)
    {
        lv.BackColor = C.Card;
        lv.ForeColor = C.Text;
        if (lv.Tag as string == "themed") { lv.Invalidate(); return; }
        lv.Tag = "themed";
        lv.BorderStyle = BorderStyle.None;
        lv.GridLines = false;
        lv.OwnerDraw = true;

        int hover = -1;
        lv.DrawColumnHeader += (s, e) =>
        {
            using var b = new SolidBrush(C.Panel);
            e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, lv.Font,
                new Point(e.Bounds.X + 10, e.Bounds.Top + 4), C.SubText);
            using var p = new Pen(C.Divider);
            e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        };
        lv.DrawSubItem += (s, e) =>
        {
            var item = e.Item;
            if (item == null) return;
            var bg = item.Selected ? C.Selection
                   : e.ItemIndex == hover ? C.RowHover
                   : C.Card;
            using var b = new SolidBrush(bg);
            e.Graphics.FillRectangle(b, e.Bounds);
            var text = e.ColumnIndex == 0 ? item.Text
                     : e.ColumnIndex < item.SubItems.Count ? item.SubItems[e.ColumnIndex].Text : "";
            var color = item.Selected ? C.SelectionText
                      : e.ColumnIndex == 0 ? C.SubText : C.Text;
            var rect = new Rectangle(e.Bounds.X + 10, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 16), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, text, lv.Font, rect, color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        lv.MouseMove += (s, e) =>
        {
            int ni = lv.GetItemAt(e.X, e.Y)?.Index ?? -1;
            if (ni == hover) return;
            int old = hover;
            hover = ni;
            if (old >= 0 && old < lv.Items.Count) lv.Invalidate(lv.GetItemRect(old));
            if (ni >= 0) lv.Invalidate(lv.GetItemRect(ni));
        };
        lv.MouseLeave += (s, e) =>
        {
            if (hover < 0) return;
            int old = hover;
            hover = -1;
            if (old < lv.Items.Count) lv.Invalidate(lv.GetItemRect(old));
        };
    }

    /// <summary>ComboBox：扁平外观；下拉列表为 DropDownList 时自绘适配主题。</summary>
    public static void StyleCombo(ComboBox cb)
    {
        cb.FlatStyle = FlatStyle.Flat;
        cb.BackColor = C.Field;
        cb.ForeColor = C.Text;
        if (cb.DropDownStyle != ComboBoxStyle.DropDownList || cb.Tag as string == "styled") return;
        cb.Tag = "styled";
        cb.DrawMode = DrawMode.OwnerDrawFixed;
        cb.DrawItem += (s, e) =>
        {
            e.DrawBackground();
            var text = e.Index >= 0 ? cb.GetItemText(cb.Items[e.Index]) : cb.Text;
            var bg = e.Index >= 0 && (e.State & DrawItemState.Selected) != 0 ? C.RowHover : C.Field;
            using var b = new SolidBrush(bg);
            e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, text, e.Font ?? cb.Font,
                new Point(e.Bounds.X + 2, e.Bounds.Y + (e.Bounds.Height - TextRenderer.MeasureText(text, e.Font ?? cb.Font).Height) / 2),
                C.Text);
        };
    }

    /// <summary>用圆角描边容器包裹输入控件，获得统一的现代输入框外观。</summary>
    public static FieldWrap Wrap(Control inner)
    {
        inner.Font = BaseFont;
        var w = new FieldWrap(inner)
        {
            Width = inner.Width + 2,
            Height = inner.Height + 2
        };
        return w;
    }

    /// <summary>菜单/上下文菜单统一渲染器与配色。</summary>
    public static void StyleMenu(ContextMenuStrip menu)
    {
        menu.Renderer = Renderer;
        menu.ForeColor = C.Text;
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
    }

    public static void ApplyTitleBar(Form f)
    {
        if (f.Tag as string != "tb")
        {
            f.Tag = "tb";
            f.HandleCreated += (s, e) => SetDarkTitle(f);
        }
        if (f.IsHandleCreated) SetDarkTitle(f);
    }

    private static void SetDarkTitle(Form f)
    {
        try
        {
            int v = Dark ? 1 : 0;
            _ = DwmSetWindowAttribute(f.Handle, 20, ref v, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            _ = DwmSetWindowAttribute(f.Handle, 19, ref v, sizeof(int)); // 旧版本 Windows
        }
        catch { }
    }

    public static Color Col(string hex) => ColorTranslator.FromHtml(hex);

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        if (r.Width < d || r.Height < d)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// <summary>双缓冲列表视图。</summary>
internal sealed class AppListView : ListView
{
    public AppListView() => DoubleBuffered = true;
}

/// <summary>圆角描边输入容器：内部控件获得统一的边框与聚焦高亮。</summary>
internal sealed class FieldWrap : Panel
{
    private readonly Control _inner;
    private bool _focused;

    public FieldWrap(Control inner)
    {
        _inner = inner;
        Padding = new Padding(1);
        BackColor = UiTheme.C.Field;
        Controls.Add(inner);
        inner.Dock = DockStyle.Fill;
        inner.GotFocus += (s, e) => { _focused = true; Invalidate(); };
        inner.LostFocus += (s, e) => { _focused = false; Invalidate(); };
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiTheme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 5);
        using var p = new Pen(_focused ? UiTheme.C.Accent : UiTheme.C.Border);
        e.Graphics.DrawPath(p, path);
    }
}

/// <summary>自绘圆角扁平按钮：语义变体 + 悬停渐变动效。</summary>
internal sealed class AppButton : Button
{
    private AppVariant _variant = AppVariant.Neutral;
    private bool _down;
    private float _anim;
    private System.Windows.Forms.Timer? _timer;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public AppVariant Variant
    {
        get => _variant;
        set { _variant = value; Invalidate(); }
    }

    public AppButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = UiTheme.ButtonFont;
        Padding = new Padding(14, 5, 14, 5);
        Margin = new Padding(2, 3, 2, 3);
        AutoSize = true;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var s = TextRenderer.MeasureText(Text, Font);
        return new Size(s.Width + Padding.Horizontal + 4, s.Height + Padding.Vertical + 4);
    }

    private (Color fill, Color hover, Color press, Color text) Palette()
    {
        var c = UiTheme.C;
        return _variant switch
        {
            AppVariant.Primary => (c.Accent, c.AccentHover, c.AccentPress, c.OnAccent),
            AppVariant.Success => (c.Success, c.SuccessHover, c.SuccessPress, c.OnSuccess),
            AppVariant.Danger => (c.Danger, c.DangerHover, c.DangerPress, c.OnDanger),
            AppVariant.Ghost => (Color.Transparent, Color.Transparent, Color.Transparent, c.Text),
            _ => (c.Field, c.RowHover, c.Border, c.Text)
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var host = Parent?.BackColor ?? UiTheme.C.Window;
        g.Clear(host);

        Color fill, text;
        var line = Color.Transparent;
        if (!Enabled)
        {
            fill = _variant == AppVariant.Ghost ? host : UiTheme.C.Field;
            text = UiTheme.C.SubText;
        }
        else
        {
            var (f, h, p, t) = Palette();
            if (_variant == AppVariant.Ghost)
            {
                fill = UiTheme.Lerp(host, UiTheme.C.RowHover, _anim * 0.9f);
                text = UiTheme.Lerp(t, UiTheme.C.Accent, _anim);
                line = Color.Transparent;
            }
            else
            {
                fill = UiTheme.Lerp(f, h, _anim);
                if (_down) fill = p;
                text = t;
                if (_variant is AppVariant.Neutral)
                    line = UiTheme.Lerp(UiTheme.C.Border, UiTheme.C.Accent, _anim);
            }
        }

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = UiTheme.Round(rect, 6))
        {
            if (fill.A > 0)
                using (var b = new SolidBrush(fill))
                    g.FillPath(b, path);
            if (line.A > 0)
                using (var pen = new Pen(line))
                    g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    private bool _animUp;

    private void Animate(bool up)
    {
        _animUp = up;
        if (_timer == null)
        {
            _timer = new System.Windows.Forms.Timer { Interval = 15 };
            _timer.Tick += (s, e) =>
            {
                _anim += _animUp ? 0.22f : -0.22f;
                if (_anim <= 0f || _anim >= 1f)
                {
                    _anim = Math.Clamp(_anim, 0f, 1f);
                    _timer.Stop();
                }
                Invalidate();
            };
        }
        if (!_timer.Enabled) _timer.Start();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Animate(true); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _down = false; Animate(false); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); _anim = 0; Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
}

/// <summary>圆角卡片面板，替代 GroupBox。</summary>
internal sealed class AppCard : Panel
{
    public string Title { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int TitleHeight { get; set; } = 34;

    public int ContentTop => TitleHeight + 2;

    public AppCard(string title)
    {
        Title = title;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var host = Parent?.BackColor ?? UiTheme.C.Window;
        g.Clear(host);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = UiTheme.Round(rect, 8))
        {
            using var b = new SolidBrush(UiTheme.C.Card);
            g.FillPath(b, path);
            using var p = new Pen(UiTheme.C.Border);
            g.DrawPath(p, path);
        }
        TextRenderer.DrawText(g, Title, UiTheme.TitleFont, new Point(14, 9), UiTheme.C.SubText);
    }
}

/// <summary>深浅色统一的菜单/状态栏渲染色表。</summary>
internal sealed class AppColorTable : ProfessionalColorTable
{
    private static ThemeColors C => UiTheme.C;

    public override Color ToolStripDropDownBackground => C.Panel;
    public override Color ToolStripBorder => C.Panel;
    public override Color ToolStripGradientBegin => C.Panel;
    public override Color ToolStripGradientMiddle => C.Panel;
    public override Color ToolStripGradientEnd => C.Panel;
    public override Color ImageMarginGradientBegin => C.Panel;
    public override Color ImageMarginGradientMiddle => C.Panel;
    public override Color ImageMarginGradientEnd => C.Panel;
    public override Color MenuBorder => C.Border;
    public override Color MenuItemBorder => C.Border;
    public override Color MenuItemSelected => C.RowHover;
    public override Color MenuItemSelectedGradientBegin => C.RowHover;
    public override Color MenuItemSelectedGradientEnd => C.RowHover;
    public override Color MenuItemPressedGradientBegin => C.Card;
    public override Color MenuItemPressedGradientEnd => C.Card;
    public override Color SeparatorDark => C.Divider;
    public override Color SeparatorLight => C.Divider;
    public override Color CheckBackground => C.Accent;
    public override Color CheckSelectedBackground => C.Accent;
    public override Color CheckPressedBackground => C.AccentPress;
    public override Color StatusStripGradientBegin => C.Panel;
    public override Color StatusStripGradientEnd => C.Panel;
    public override Color GripDark => C.Divider;
    public override Color GripLight => C.Border;
}
