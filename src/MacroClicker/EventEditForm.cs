namespace MacroClicker;

/// <summary>事件编辑/插入对话框：按事件类型动态生成编辑字段，直接修改传入的事件对象。</summary>
internal sealed class EventEditForm : Form
{
    private readonly MacroEvent _ev;
    private readonly NumericUpDown _numDelay;
    private NumericUpDown? _numX, _numY, _numDelta;
    private ComboBox? _cmbButton, _cmbKey;
    private TextBox? _txtCombo;
    private readonly List<CheckBox> _modChecks = new();

    public EventEditForm(MacroEvent ev)
    {
        _ev = ev;
        Text = "编辑事件";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            Padding = new Padding(14, 12, 14, 6)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control c)
        {
            table.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(4, 8, 4, 8)
            });
            c.Anchor = AnchorStyles.Left;
            table.Controls.Add(c);
        }

        _numDelay = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 86400,
            DecimalPlaces = 3,
            Increment = 0.1M,
            Value = Math.Min(86400, Math.Max(0, (decimal)ev.Delay)),
            Width = 140
        };
        Row("执行前间隔(秒)", _numDelay);

        bool hasPos = ev.Type is EventType.MouseClick or EventType.MouseDown or EventType.MouseUp
                      or EventType.MouseMove or EventType.Wheel;
        if (hasPos)
        {
            _numX = new NumericUpDown { Minimum = -50000, Maximum = 50000, Value = Math.Clamp(ev.X, -50000, 50000), Width = 90 };
            _numY = new NumericUpDown { Minimum = -50000, Maximum = 50000, Value = Math.Clamp(ev.Y, -50000, 50000), Width = 90 };
            var p = new FlowLayoutPanel { WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            p.Controls.Add(new Label { Text = "X:", AutoSize = true, Margin = new Padding(0, 9, 2, 0) });
            p.Controls.Add(_numX);
            p.Controls.Add(new Label { Text = "Y:", AutoSize = true, Margin = new Padding(8, 9, 2, 0) });
            p.Controls.Add(_numY);
            Row("坐标", p);
        }

        if (ev.Type is EventType.MouseClick or EventType.MouseDown or EventType.MouseUp)
        {
            _cmbButton = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            _cmbButton.Items.AddRange(new object[] { "左键", "右键", "中键", "侧键1", "侧键2" });
            _cmbButton.SelectedIndex = ev.Button switch { "right" => 1, "middle" => 2, "x1" => 3, "x2" => 4, _ => 0 };
            Row("鼠标按键", _cmbButton);
        }

        if (ev.Type == EventType.Wheel)
        {
            _numDelta = new NumericUpDown
            {
                Minimum = -12000,
                Maximum = 12000,
                Increment = 120,
                Value = Math.Clamp(ev.Delta == 0 ? 120 : ev.Delta, -12000, 12000),
                Width = 100
            };
            Row("滚轮量(±120/格)", _numDelta);
        }

        if (ev.Type is EventType.MouseClick or EventType.MouseDown or EventType.MouseUp or EventType.Wheel)
        {
            var p = new FlowLayoutPanel { WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            foreach (var (label, vk) in new (string, uint)[] { ("Ctrl", 0x11u), ("Shift", 0x10u), ("Alt", 0x12u), ("Win", 0x5Bu) })
            {
                var ck = new CheckBox
                {
                    Text = label,
                    AutoSize = true,
                    Checked = ev.Modifiers.Contains(vk),
                    Margin = new Padding(0, 5, 10, 0)
                };
                ck.Tag = vk;
                _modChecks.Add(ck);
                p.Controls.Add(ck);
            }
            Row("附加修饰键", p);
        }

        if (ev.Type == EventType.Key)
        {
            _cmbKey = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 140, Text = KeyMap.NameOf(ev.Vk) };
            _cmbKey.Items.AddRange(new object[]
            {
                "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
                "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
                "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
                "enter", "esc", "space", "tab", "backspace", "delete", "insert",
                "home", "end", "pgup", "pgdn", "up", "down", "left", "right",
                "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12"
            });
            Row("按键名称", _cmbKey);
        }

        if (ev.Type == EventType.Hotkey)
        {
            _txtCombo = new TextBox { Width = 200, Text = string.Join("+", ev.Combo.Select(KeyMap.NameOf)) };
            Row("组合键(用+分隔)", _txtCombo);
        }

        if (ev.Type == EventType.Wait)
        {
            Row("说明", new Label { Text = "等待事件：执行到该行时停留上方设置的秒数", AutoSize = true });
        }

        var btnOk = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
        var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        btnOk.Click += (s, e) =>
        {
            if (!ValidateAndApply()) DialogResult = DialogResult.None;
        };

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8, 6, 8, 8)
        };
        bottom.Controls.Add(btnCancel);
        bottom.Controls.Add(btnOk);

        Controls.Add(table);
        Controls.Add(bottom);
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        var height = table.PreferredSize.Height + bottom.PreferredSize.Height + 44;
        ClientSize = new Size(420, height);
    }

    private bool ValidateAndApply()
    {
        if (_cmbKey != null && !KeyMap.TryParse(_cmbKey.Text, out _))
        {
            MessageBox.Show("无法识别按键名称: " + _cmbKey.Text, "提示");
            _cmbKey.Focus();
            return false;
        }
        if (_txtCombo != null)
        {
            var parts = _txtCombo.Text.Split('+', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                MessageBox.Show("组合键至少需要两个按键，用 + 分隔，例如 ctrl+s", "提示");
                _txtCombo.Focus();
                return false;
            }
            foreach (var p in parts)
            {
                if (!KeyMap.TryParse(p.Trim(), out _))
                {
                    MessageBox.Show("无法识别按键: " + p, "提示");
                    _txtCombo.Focus();
                    return false;
                }
            }
        }

        _ev.Delay = (double)_numDelay.Value;
        if (_numX != null) _ev.X = (int)_numX.Value;
        if (_numY != null) _ev.Y = (int)_numY.Value;
        if (_numDelta != null) _ev.Delta = (int)_numDelta.Value;
        if (_cmbButton != null)
            _ev.Button = _cmbButton.SelectedIndex switch { 1 => "right", 2 => "middle", 3 => "x1", 4 => "x2", _ => "left" };
        if (_cmbKey != null && KeyMap.TryParse(_cmbKey.Text, out var vk)) _ev.Vk = vk;
        if (_txtCombo != null)
        {
            var combo = new List<uint>();
            foreach (var p in _txtCombo.Text.Split('+', StringSplitOptions.RemoveEmptyEntries))
                if (KeyMap.TryParse(p.Trim(), out var kvk)) combo.Add(kvk);
            _ev.Combo = combo;
        }
        if (_modChecks.Count > 0)
        {
            var mods = new List<uint>();
            foreach (var ck in _modChecks)
                if (ck.Checked) mods.Add((uint)ck.Tag!);
            _ev.Modifiers = mods;
        }
        return true;
    }
}
