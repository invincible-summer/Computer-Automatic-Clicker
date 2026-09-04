using System.Drawing.Drawing2D;

namespace MacroClicker.Emulator;

/// <summary>
/// 模拟器截图取点：展示设备当前画面，点击图片位置即生成
/// 「device 坐标」的鼠标点击事件（存入宏后由模拟器会话直接注入，无需对齐窗口）。
/// </summary>
internal sealed class EmuShotDialog : Form
{
    public List<MacroEvent> Picked { get; } = new();

    private readonly Bitmap _shot;
    private readonly List<Point> _marks = new();
    private readonly PictureBox _pb;
    private readonly Label _hint;

    public EmuShotDialog(Bitmap shot, string caption)
    {
        _shot = shot;
        Text = "模拟器截图取点";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = UiTheme.BaseFont;
        MinimumSize = new Size(640, 500);
        ClientSize = new Size(860, 640);
        DoubleBuffered = true;
        KeyPreview = true;

        _hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 4, 10, 0),
            Tag = "sub",
            Text = caption
        };

        _pb = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = _shot,
            BackColor = UiTheme.C.Field
        };
        _pb.MouseClick += OnPick;
        _pb.Paint += OnPaintMarks;

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(10, 8, 10, 8)
        };
        var btnDone = new AppButton { Text = "完成", Variant = AppVariant.Primary };
        var btnClear = new AppButton { Text = "清除标记", Variant = AppVariant.Ghost };
        btnClear.Click += (s, e) => { Picked.Clear(); _marks.Clear(); _pb.Invalidate(); UpdateHint(); };
        btnDone.Click += (s, e) => Close();
        bottom.Controls.Add(btnDone);
        bottom.Controls.Add(btnClear);

        Controls.Add(_pb);
        Controls.Add(_hint);
        Controls.Add(bottom);

        UiTheme.Apply(this);
        UpdateHint();
    }

    private void UpdateHint()
    {
        _hint.Text = $"点击画面取点（{_shot.Width}×{_shot.Height}，已取 {_marks.Count} 点）→ 生成模拟器点击事件";
    }

    /// <summary>Zoom 模式下 图片像素 ↔ 控件像素 的换算。</summary>
    private (double Scale, int OffX, int OffY) ComputeLayout()
    {
        double scale = Math.Min(_pb.Width / (double)_shot.Width, _pb.Height / (double)_shot.Height);
        int offX = (int)((_pb.Width - _shot.Width * scale) / 2);
        int offY = (int)((_pb.Height - _shot.Height * scale) / 2);
        return (scale, offX, offY);
    }

    private void OnPick(object? s, MouseEventArgs e)
    {
        var (scale, offX, offY) = ComputeLayout();
        int dx = (int)Math.Round((e.X - offX) / scale);
        int dy = (int)Math.Round((e.Y - offY) / scale);
        if (dx < 0 || dy < 0 || dx >= _shot.Width || dy >= _shot.Height) return;
        _marks.Add(new Point(dx, dy));
        Picked.Add(new MacroEvent
        {
            Type = EventType.MouseClick,
            X = dx,
            Y = dy,
            Delay = 0.3,
            CoordSpace = "device"
        });
        _pb.Invalidate();
        UpdateHint();
    }

    private void OnPaintMarks(object? s, PaintEventArgs e)
    {
        if (_marks.Count == 0) return;
        var (scale, offX, offY) = ComputeLayout();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < _marks.Count; i++)
        {
            var p = _marks[i];
            float cx = (float)(offX + p.X * scale);
            float cy = (float)(offY + p.Y * scale);
            float r = 9f;
            using var ring = new Pen(Color.FromArgb(230, 229, 72, 84), 2.5f);
            e.Graphics.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
            e.Graphics.DrawLine(ring, cx - r - 5, cy, cx - r + 2, cy);
            e.Graphics.DrawLine(ring, cx + r - 2, cy, cx + r + 5, cy);
            e.Graphics.DrawLine(ring, cx, cy - r - 5, cx, cy - r + 2);
            e.Graphics.DrawLine(ring, cx, cy + r - 2, cx, cy + r + 5);
            TextRenderer.DrawText(e.Graphics, (i + 1).ToString(), UiTheme.TitleFont,
                new Point((int)(cx + r + 4), (int)(cy - r)), Color.White);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 保留 _shot 由调用方负责释放
        _pb.Image = null;
        base.OnFormClosing(e);
    }
}
