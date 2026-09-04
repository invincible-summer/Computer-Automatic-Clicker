using System.Globalization;
using System.Text.Json;

namespace MacroClicker;

internal sealed class MainForm : Form
{
    private enum AppState { Idle, Recording, Playing, Paused }
    private enum StatusKind { Info, Good, Warn, Bad }

    private readonly Recorder _recorder = new();
    private readonly Player _player = new();

    private readonly AppListView _lv;
    private readonly AppButton _btnRecord, _btnStopRec, _btnPlay, _btnPause, _btnStopPlay;
    private readonly AppButton _btnSave, _btnLoad, _btnClear, _btnTheme;
    private readonly TextBox _txtName;
    private readonly ComboBox _cmbMode, _cmbSpeed;
    private readonly NumericUpDown _numCount, _numInterval, _numCountdown;
    private readonly CheckBox _ckKeys, _ckClicks, _ckWheel, _ckDrags, _ckMoves, _ckFailsafe;
    private readonly AppCard _gRec, _gPlay;
    private readonly Panel _toolbar;
    private ContextMenuStrip? _menu;
    private readonly ToolStripStatusLabel _lblStatus, _lblCount, _lblHotkeys;
    private StatusKind _statusKind = StatusKind.Info;

    private AppState _state = AppState.Idle;
    private bool _failSafeTriggered;

    private List<MacroEvent> MacroEvents => _recorder.Events;

    public MainForm()
    {
        UiTheme.SetDark(MacroStore.ReadThemeDark());
        Text = "宏连点器 · Macro Clicker";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1060, 640);
        MinimumSize = new Size(920, 540);
        Font = UiTheme.BaseFont;
        KeyPreview = false;
        DoubleBuffered = true;

        // ---------- 顶部工具栏 ----------
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(10, 12, 10, 6),
            WrapContents = false
        };
        _toolbar = toolbar;
        _btnRecord = MkBtn("● 录制", AppVariant.Success);
        _btnStopRec = MkBtn("■ 停录", AppVariant.Neutral);
        _btnPlay = MkBtn("▶ 执行", AppVariant.Primary);
        _btnPause = MkBtn("⏸ 暂停", AppVariant.Neutral);
        _btnStopPlay = MkBtn("⏹ 停止", AppVariant.Danger);
        _btnSave = MkBtn("保存", AppVariant.Ghost);
        _btnLoad = MkBtn("打开", AppVariant.Ghost);
        _btnClear = MkBtn("清空", AppVariant.Ghost);
        _btnTheme = MkBtn(UiTheme.Dark ? "☀ 浅色" : "🌙 深色", AppVariant.Ghost);
        _btnTheme.Margin = new Padding(14, 3, 2, 3);

        var tt = new ToolTip();
        tt.SetToolTip(_btnRecord, "开始录制 (F6)");
        tt.SetToolTip(_btnStopRec, "停止录制 (F7)");
        tt.SetToolTip(_btnPlay, "开始执行 (F8)");
        tt.SetToolTip(_btnPause, "暂停 / 继续 (F9)");
        tt.SetToolTip(_btnStopPlay, "停止一切执行与录制 (F10)");
        tt.SetToolTip(_btnTheme, "切换深色 / 浅色主题");

        _txtName = new TextBox { Width = 150, Margin = new Padding(10, 5, 2, 5), Text = "" };
        var nameWrap = UiTheme.Wrap(_txtName);
        nameWrap.Margin = new Padding(10, 8, 2, 8);
        toolbar.Controls.AddRange(new Control[]
        {
            _btnRecord, _btnStopRec, _btnPlay, _btnPause, _btnStopPlay,
            new Panel { Width = 2, Height = 26, Margin = new Padding(10, 9, 10, 9), BackColor = UiTheme.C.Divider },
            new Label { Text = "宏名称:", AutoSize = true, Margin = new Padding(2, 13, 2, 0) },
            nameWrap, _btnSave, _btnLoad, _btnClear, _btnTheme
        });

        // ---------- 事件列表 ----------
        _lv = new AppListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = true
        };
        _lv.Columns.Add("#", 46);
        _lv.Columns.Add("操作", 240);
        _lv.Columns.Add("间隔(秒)", 90);
        _lv.Columns.Add("参数", 230);
        UiTheme.StyleList(_lv);
        _lv.ContextMenuStrip = BuildMenu();
        _lv.DoubleClick += (s, e) => EditSelected();
        _lv.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
        };

        // ---------- 右侧设置面板 ----------
        var right = new Panel { Dock = DockStyle.Right, Width = 322, Padding = new Padding(10, 12, 12, 10) };

        _gRec = new AppCard("录制选项") { Dock = DockStyle.Top, Height = 196 };
        int ry = _gRec.ContentTop + 4;
        CheckBox AddCk(string text, bool check)
        {
            var ck = new CheckBox { Text = text, AutoSize = true, Location = new Point(16, ry) };
            _gRec.Controls.Add(ck);
            ry += 30;
            return ck;
        }
        _ckKeys = AddCk("记录键盘输入（按键 / 组合键）", true);
        _ckClicks = AddCk("记录鼠标点击（左/右/中/侧键）", true);
        _ckWheel = AddCk("记录滚轮", true);
        _ckDrags = AddCk("记录拖拽（按住并移动）", true);
        _ckMoves = AddCk("记录空闲鼠标移动（事件量大）", false);

        _gPlay = new AppCard("执行设置") { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 6) };
        int py = _gPlay.ContentTop + 4;
        Control Row(string label, Control c, int width)
        {
            c.Font = UiTheme.BaseFont;
            c.Width = width;
            var wrap = UiTheme.Wrap(c);
            wrap.Location = new Point(118, py - 1);
            _gPlay.Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(16, py + 4) });
            _gPlay.Controls.Add(wrap);
            py += 36;
            return c;
        }

        _cmbMode = (ComboBox)Row("执行模式:", new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Items = { "执行一次", "执行指定次数", "无限循环" }
        }, 160);
        _cmbMode.SelectedIndex = 0;

        _numCount = (NumericUpDown)Row("循环次数:", new NumericUpDown
        {
            Minimum = 1, Maximum = 999999, Value = 10
        }, 90);
        _cmbMode.SelectedIndexChanged += (s, e) => _numCount.Enabled = _cmbMode.SelectedIndex == 1;
        _numInterval = (NumericUpDown)Row("循环间隔(秒):", new NumericUpDown
        {
            Minimum = 0, Maximum = 3600, DecimalPlaces = 2, Increment = 0.5M, Value = 0
        }, 90);
        _cmbSpeed = (ComboBox)Row("播放速度:", new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Items = { "0.25x", "0.5x", "1x", "2x", "4x", "8x" }
        }, 90);
        _cmbSpeed.SelectedIndex = 2;
        _numCountdown = (NumericUpDown)Row("播放前倒计时(秒):", new NumericUpDown
        {
            Minimum = 0, Maximum = 10, Value = 0
        }, 90);
        _ckFailsafe = new CheckBox
        {
            Text = "紧急停止：鼠标移到屏幕左上角",
            AutoSize = true,
            Location = new Point(16, py + 5),
            Checked = true
        };
        _gPlay.Controls.Add(_ckFailsafe);
        py += 36;
        var hint = new Label
        {
            Text = "提示：每行的“间隔”表示执行该行之前\n等待的时间；回放时该时间会除以播放速度。",
            AutoSize = true,
            Tag = "sub",
            Location = new Point(16, py + 5)
        };
        _gPlay.Controls.Add(hint);

        right.Controls.Add(_gPlay);
        right.Controls.Add(_gRec);

        // ---------- 状态栏 ----------
        var status = new StatusStrip();
        _lblStatus = new ToolStripStatusLabel("就绪") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _lblCount = new ToolStripStatusLabel("事件 0");
        _lblHotkeys = new ToolStripStatusLabel("F6 录制 · F7 停录 · F8 执行 · F9 暂停/继续 · F10 停止") { ForeColor = UiTheme.C.SubText };
        status.Items.AddRange(new ToolStripItem[] { _lblStatus, _lblCount, _lblHotkeys });
        status.Renderer = UiTheme.Renderer;
        status.BackColor = UiTheme.C.Panel;
        status.SizingGrip = false;

        // Fill 控件先加入，再依次加入边缘停靠控件
        Controls.Add(_lv);
        Controls.Add(right);
        Controls.Add(toolbar);
        Controls.Add(status);

        // ---------- 事件绑定 ----------
        _recorder.EventRecorded += ev =>
        {
            _lv.Items.Add(MakeItem(ev, MacroEvents.Count - 1));
            _lblCount.Text = $"事件 {MacroEvents.Count}";
        };

        _player.Status += s => Ui(() => SetStatus(s, StatusKind.Good));
        _player.AbortedByFailSafe += () => { _failSafeTriggered = true; };
        _player.Finished += ok => Ui(() =>
        {
            var fs = _failSafeTriggered;
            _failSafeTriggered = false;
            SetState(AppState.Idle);
            SetStatus(fs ? "⛔ 已触发紧急停止（鼠标左上角）" : ok ? "✔ 执行完成" : "■ 已停止",
                fs || !ok ? StatusKind.Bad : StatusKind.Good);
        });

        _btnRecord.Click += (s, e) => StartRecording();
        _btnStopRec.Click += (s, e) => StopRecording(true);
        _btnPlay.Click += (s, e) => StartPlayback();
        _btnPause.Click += (s, e) => TogglePause();
        _btnStopPlay.Click += (s, e) => StopAll();
        _btnClear.Click += (s, e) => ClearAll();
        _btnSave.Click += (s, e) => SaveMacro();
        _btnLoad.Click += (s, e) => LoadMacro();
        _btnTheme.Click += (s, e) => UiTheme.SetDark(!UiTheme.Dark);

        UiTheme.Changed += OnThemeChanged;
        UiTheme.Apply(this);
        RefreshStatus();
        LoadSettings();
        SetState(AppState.Idle);
        RebuildList();
    }

    private void OnThemeChanged()
    {
        UiTheme.Apply(this);
        if (_menu != null) UiTheme.StyleMenu(_menu);
        _toolbar.BackColor = UiTheme.C.Panel;
        _btnTheme.Text = UiTheme.Dark ? "☀ 浅色" : "🌙 深色";
        RefreshStatus();
    }

    private void SetStatus(string text, StatusKind kind)
    {
        _statusKind = kind;
        _lblStatus.Text = text;
        RefreshStatusColor();
    }

    private void RefreshStatus()
    {
        _lblCount.ForeColor = UiTheme.C.SubText;
        _lblHotkeys.ForeColor = UiTheme.C.SubText;
        RefreshStatusColor();
    }

    private void RefreshStatusColor()
    {
        var c = UiTheme.C;
        _lblStatus.ForeColor = _statusKind switch
        {
            StatusKind.Good => c.Success,
            StatusKind.Warn => c.Warning,
            StatusKind.Bad => c.Danger,
            _ => c.Text
        };
    }

    private static AppButton MkBtn(string text, AppVariant variant) => new()
    {
        Text = text,
        Variant = variant,
        AutoSize = true,
        Padding = new Padding(14, 5, 14, 5),
        Margin = new Padding(2, 3, 2, 3)
    };

    private void Ui(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) a(); }); } catch { }
        }
        else a();
    }

    // ================= 状态机 =================

    private void SetState(AppState s)
    {
        _state = s;
        var idle = s == AppState.Idle;
        _btnRecord.Enabled = idle;
        _btnStopRec.Enabled = s == AppState.Recording;
        _btnPlay.Enabled = idle;
        _btnPause.Enabled = s is AppState.Playing or AppState.Paused;
        _btnPause.Text = s == AppState.Paused ? "⏵ 继续" : "⏸ 暂停";
        _btnPause.Variant = s == AppState.Paused ? AppVariant.Success : AppVariant.Neutral;
        _btnStopPlay.Enabled = s is AppState.Playing or AppState.Paused or AppState.Recording;
        _btnSave.Enabled = idle;
        _btnLoad.Enabled = idle;
        _btnClear.Enabled = idle;
        _gRec.Enabled = idle;
        _gPlay.Enabled = idle;
        _numCount.Enabled = idle && _cmbMode.SelectedIndex == 1;
        _txtName.Enabled = idle;
        if (idle)
        {
            _lblCount.Text = $"事件 {MacroEvents.Count}";
            if (_lblStatus.Text is "就绪" or "" or "● 录制中…（按 F7 或点击“停止录制”结束）") SetStatus("就绪", StatusKind.Info);
        }
    }

    // ================= 录制 =================

    private void StartRecording()
    {
        if (_state is AppState.Recording or AppState.Playing or AppState.Paused) return;
        if (MacroEvents.Count > 0 &&
            MessageBox.Show("开始新录制会清空当前事件列表，继续吗？", "确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        MacroEvents.Clear();
        RebuildList();
        _recorder.OwnWindow = Handle;

        var opts = new RecordOptions
        {
            RecordKeyboard = _ckKeys.Checked,
            RecordMouseClicks = _ckClicks.Checked,
            RecordWheel = _ckWheel.Checked,
            RecordDrags = _ckDrags.Checked,
            RecordMouseMove = _ckMoves.Checked
        };
        try { _recorder.Start(opts); }
        catch (Exception ex)
        {
            MessageBox.Show("启动录制失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        SetState(AppState.Recording);
        SetStatus("● 录制中…（按 F7 或点击“停止录制”结束）", StatusKind.Bad);
    }

    private void StopRecording(bool notify)
    {
        if (_state != AppState.Recording) return;
        _recorder.Stop();
        SetState(AppState.Idle);
        SetStatus(notify ? $"✔ 录制完成，共 {MacroEvents.Count} 个事件" : "■ 录制已停止", StatusKind.Info);
    }

    // ================= 执行 =================

    private void StartPlayback()
    {
        if (_state is AppState.Playing or AppState.Paused or AppState.Recording) return;
        if (MacroEvents.Count == 0)
        {
            SetStatus("没有可执行的事件，请先录制或打开宏文件", StatusKind.Bad);
            return;
        }
        double speed = 1.0;
        var speedText = _cmbSpeed.Text.TrimEnd('x', 'X', ' ');
        if (double.TryParse(speedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0) speed = v;

        var settings = new PlaySettings
        {
            Mode = (LoopMode)_cmbMode.SelectedIndex,
            Count = (int)_numCount.Value,
            LoopInterval = (double)_numInterval.Value,
            Speed = speed,
            CountdownSeconds = (int)_numCountdown.Value,
            FailSafe = _ckFailsafe.Checked
        };
        _failSafeTriggered = false;
        var snapshot = new List<MacroEvent>(MacroEvents);
        SetState(AppState.Playing);
        SetStatus("▶ 开始执行…", StatusKind.Good);
        _player.Start(snapshot, settings);
    }

    private void TogglePause()
    {
        if (!_player.IsBusy) return;
        if (_state == AppState.Playing)
        {
            _player.Pause();
            SetState(AppState.Paused);
            SetStatus("⏸ 已暂停（F9 继续）", StatusKind.Warn);
        }
        else if (_state == AppState.Paused)
        {
            _player.Resume();
            SetState(AppState.Playing);
            SetStatus("▶ 继续执行…", StatusKind.Good);
        }
    }

    private void StopAll()
    {
        if (_state == AppState.Recording) StopRecording(false);
        if (_state is AppState.Playing or AppState.Paused) _player.Stop();
    }

    // ================= 列表操作 =================

    private ListViewItem MakeItem(MacroEvent e, int idx)
    {
        var it = new ListViewItem((idx + 1).ToString()) { Tag = e };
        it.SubItems.Add(e.Display);
        it.SubItems.Add(e.Delay.ToString("0.###"));
        it.SubItems.Add(e.Params);
        return it;
    }

    private void RebuildList()
    {
        _lv.BeginUpdate();
        _lv.Items.Clear();
        for (int i = 0; i < MacroEvents.Count; i++) _lv.Items.Add(MakeItem(MacroEvents[i], i));
        _lv.EndUpdate();
        _lblCount.Text = $"事件 {MacroEvents.Count}";
    }

    private void SelectIndex(int i)
    {
        if (i >= 0 && i < _lv.Items.Count)
        {
            _lv.Items[i].Selected = true;
            _lv.Items[i].EnsureVisible();
        }
    }

    private void EditSelected()
    {
        if (_state != AppState.Idle || _lv.SelectedIndices.Count != 1) return;
        int i = _lv.SelectedIndices[0];
        using var dlg = new EventEditForm(MacroEvents[i]);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            RebuildList();
            SelectIndex(i);
        }
    }

    private void DeleteSelected()
    {
        if (_state != AppState.Idle || _lv.SelectedIndices.Count == 0) return;
        foreach (int i in _lv.SelectedIndices.Cast<int>().OrderByDescending(x => x))
            MacroEvents.RemoveAt(i);
        RebuildList();
    }

    private void MoveSelected(int delta)
    {
        if (_state != AppState.Idle || _lv.SelectedIndices.Count != 1) return;
        int i = _lv.SelectedIndices[0];
        int j = i + delta;
        if (j < 0 || j >= MacroEvents.Count) return;
        (MacroEvents[i], MacroEvents[j]) = (MacroEvents[j], MacroEvents[i]);
        RebuildList();
        SelectIndex(j);
    }

    private void CopySelected()
    {
        if (_state != AppState.Idle || _lv.SelectedIndices.Count != 1) return;
        int i = _lv.SelectedIndices[0];
        MacroEvents.Insert(i + 1, MacroEvents[i].Clone());
        RebuildList();
        SelectIndex(i + 1);
    }

    private void InsertEvent(MacroEvent ev)
    {
        if (_state != AppState.Idle) return;
        int i = _lv.SelectedIndices.Count == 1 ? _lv.SelectedIndices[0] + 1 : MacroEvents.Count;
        using var dlg = new EventEditForm(ev);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            MacroEvents.Insert(i, ev);
            RebuildList();
            SelectIndex(i);
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        _menu = m;
        var edit = new ToolStripMenuItem("编辑…");
        var del = new ToolStripMenuItem("删除 (Del)");
        var up = new ToolStripMenuItem("上移");
        var down = new ToolStripMenuItem("下移");
        var copy = new ToolStripMenuItem("复制");
        var insClick = new ToolStripMenuItem("插入鼠标点击…");
        var insKey = new ToolStripMenuItem("插入按键…");
        var insWait = new ToolStripMenuItem("插入等待…");

        edit.Click += (s, e) => EditSelected();
        del.Click += (s, e) => DeleteSelected();
        up.Click += (s, e) => MoveSelected(-1);
        down.Click += (s, e) => MoveSelected(1);
        copy.Click += (s, e) => CopySelected();
        insClick.Click += (s, e) =>
        {
            Win32.GetCursorPos(out var p);
            InsertEvent(new MacroEvent { Type = EventType.MouseClick, X = p.X, Y = p.Y, Delay = 0 });
        };
        insKey.Click += (s, e) =>
            InsertEvent(new MacroEvent { Type = EventType.Key, Vk = 0x0D, Delay = 0 });
        insWait.Click += (s, e) =>
            InsertEvent(new MacroEvent { Type = EventType.Wait, Delay = 1 });

        m.Items.AddRange(new ToolStripItem[]
        {
            edit, del, new ToolStripSeparator(), up, down, copy,
            new ToolStripSeparator(), insClick, insKey, insWait
        });

        m.Opening += (s, e) =>
        {
            bool idle = _state == AppState.Idle;
            bool one = idle && _lv.SelectedIndices.Count == 1;
            bool any = idle && _lv.SelectedIndices.Count > 0;
            edit.Enabled = one;
            del.Enabled = any;
            up.Enabled = one;
            down.Enabled = one;
            copy.Enabled = one;
            insClick.Enabled = insKey.Enabled = insWait.Enabled = idle;
        };
        UiTheme.StyleMenu(m);
        return m;
    }

    // ================= 保存 / 打开 =================

    private void SaveMacro()
    {
        if (_state != AppState.Idle) return;
        if (MacroEvents.Count == 0)
        {
            MessageBox.Show("没有事件可保存。");
            return;
        }
        Directory.CreateDirectory(MacroStore.MacrosDir);
        var defaultName = _txtName.Text.Trim().Length > 0 ? _txtName.Text.Trim() : "macro";
        using var dlg = new SaveFileDialog
        {
            Title = "保存宏",
            Filter = "宏文件 (*.json)|*.json",
            InitialDirectory = MacroStore.MacrosDir,
            FileName = defaultName + ".json",
            OverwritePrompt = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var name = Path.GetFileNameWithoutExtension(dlg.FileName);
            MacroStore.Save(dlg.FileName, name, MacroEvents);
            _txtName.Text = name;
            SetStatus($"✔ 已保存 {MacroEvents.Count} 个事件 → {dlg.FileName}", StatusKind.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadMacro()
    {
        if (_state != AppState.Idle) return;
        Directory.CreateDirectory(MacroStore.MacrosDir);
        using var dlg = new OpenFileDialog
        {
            Title = "打开宏",
            Filter = "宏文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            InitialDirectory = MacroStore.MacrosDir
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var (name, list) = MacroStore.Load(dlg.FileName);
            MacroEvents.Clear();
            MacroEvents.AddRange(list);
            _txtName.Text = name;
            RebuildList();
            SetStatus($"✔ 已加载 {list.Count} 个事件", StatusKind.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show("加载失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ClearAll()
    {
        if (_state != AppState.Idle || MacroEvents.Count == 0) return;
        if (MessageBox.Show("确定清空全部事件？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        MacroEvents.Clear();
        RebuildList();
    }

    // ================= 设置持久化 =================

    private void SaveSettings()
    {
        try
        {
            var dto = new MacroStore.AppSettings
            {
                LoopMode = _cmbMode.SelectedIndex,
                LoopCount = (int)_numCount.Value,
                LoopInterval = (double)_numInterval.Value,
                Speed = _cmbSpeed.Text,
                Countdown = (int)_numCountdown.Value,
                FailSafe = _ckFailsafe.Checked,
                RecKeys = _ckKeys.Checked,
                RecClicks = _ckClicks.Checked,
                RecWheel = _ckWheel.Checked,
                RecDrags = _ckDrags.Checked,
                RecMoves = _ckMoves.Checked,
                LastName = _txtName.Text,
                Theme = UiTheme.Dark ? "dark" : "light"
            };
            Directory.CreateDirectory(MacroStore.MacrosDir);
            File.WriteAllText(Path.Combine(MacroStore.MacrosDir, "config.json"),
                JsonSerializer.Serialize(dto, MacroStore.JsonOpts));
        }
        catch { }
    }

    private void LoadSettings()
    {
        try
        {
            var p = Path.Combine(MacroStore.MacrosDir, "config.json");
            if (!File.Exists(p)) return;
            var dto = JsonSerializer.Deserialize<MacroStore.AppSettings>(File.ReadAllText(p), MacroStore.JsonOpts);
            if (dto == null) return;
            _cmbMode.SelectedIndex = Math.Clamp(dto.LoopMode, 0, 2);
            _numCount.Value = Math.Clamp(dto.LoopCount, 1, 999999);
            _numInterval.Value = (decimal)Math.Clamp(dto.LoopInterval, 0.0, 3600.0);
            var si = _cmbSpeed.Items.IndexOf(dto.Speed ?? "1x");
            if (si >= 0) _cmbSpeed.SelectedIndex = si;
            _numCountdown.Value = Math.Clamp(dto.Countdown, 0, 10);
            _ckFailsafe.Checked = dto.FailSafe;
            _ckKeys.Checked = dto.RecKeys;
            _ckClicks.Checked = dto.RecClicks;
            _ckWheel.Checked = dto.RecWheel;
            _ckDrags.Checked = dto.RecDrags;
            _ckMoves.Checked = dto.RecMoves;
            if (!string.IsNullOrEmpty(dto.LastName)) _txtName.Text = dto.LastName;
        }
        catch { }
    }

    // ================= 全局热键 =================

    private static readonly uint[] HotkeyVks = { 0x75, 0x76, 0x77, 0x78, 0x79 }; // F6..F10

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var failed = new List<string>();
        for (int i = 0; i < HotkeyVks.Length; i++)
        {
            if (!Win32.RegisterHotKey(Handle, i + 1, 0, HotkeyVks[i]))
                failed.Add($"F{6 + i}");
        }
        if (failed.Count > 0)
            SetStatus("⚠ 快捷键注册失败（可能被其他程序占用）: " + string.Join(", ", failed), StatusKind.Warn);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case 1: // F6
                    if (_state == AppState.Recording) StopRecording(true);
                    else StartRecording();
                    break;
                case 2: StopRecording(true); break;                       // F7
                case 3:                                                   // F8
                    if (_state == AppState.Paused) TogglePause();
                    else StartPlayback();
                    break;
                case 4: TogglePause(); break;                             // F9
                case 5: StopAll(); break;                                 // F10
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopAll();
        _player.Stop();
        _recorder.Stop();
        _recorder.Dispose();
        for (int i = 1; i <= HotkeyVks.Length; i++) Win32.UnregisterHotKey(Handle, i);
        UiTheme.Changed -= OnThemeChanged;
        SaveSettings();
        base.OnFormClosing(e);
    }
}
