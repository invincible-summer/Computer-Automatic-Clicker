using System.Globalization;
using System.Text.Json;
using MacroClicker.Emulator;

namespace MacroClicker;

/// <summary>
/// 主窗口：双页面设计 —— 「本机 Windows」与本机鼠标键盘交互；
/// 「模拟器 ADB」为独立完整页面，通过 ADB 注入输入，不占用本机鼠标。
/// 顶部工具栏与右侧设置面板作用于当前页面。
/// </summary>
internal sealed class MainForm : Form
{
    private enum AppState { Idle, Recording, Playing, Paused }
    private enum StatusKind { Info, Good, Warn, Bad }

    private readonly Recorder _recorder = new();
    private readonly Player _player = new();

    // ---- 数据 ----
    private readonly List<MacroEvent> _winEvents = new();
    private readonly List<MacroEvent> _emuEvents = new();
    private MacroStore.TargetSettings _winSettings = new();
    private MacroStore.TargetSettings _emuSettings = new();
    /// <summary>当前 UI 展示/编辑的目标页（切页时先把面板值快照回离开的目标）。</summary>
    private MacroTarget _curTarget = MacroTarget.Windows;

    // ---- 模拟器（ADB）----
    private EmulatorSession? _emu;
    private AdbClient? _adb;
    private List<EmulatorCandidate> _emuCandidates = new();
    private Func<List<MuMuInstance>>? _mumuRequery;
    private bool _discovering;

    // ---- 控件 ----
    private readonly AppListView _lvWin, _lvEmu;
    private readonly AppButton _btnRecord, _btnStopRec, _btnPlay, _btnPause, _btnStopPlay;
    private readonly AppButton _btnSave, _btnDelete, _btnTheme;
    private readonly ComboBox _cmbMacro, _cmbDevice;
    private readonly AppButton _btnRefresh, _btnConnect, _btnDisconnect;
    private readonly Label _lblDevice;
    private readonly ComboBox _cmbMode, _cmbSpeed;
    private readonly NumericUpDown _numCount, _numInterval, _numCountdown;
    private readonly CheckBox _ckKeys, _ckClicks, _ckWheel, _ckDrags, _ckFailsafe;
    private readonly AppCard _gRec, _gPlay;
    private readonly Panel _toolbar, _emuStrip;
    private readonly TabControl _tabs;
    private ContextMenuStrip? _menu;
    private readonly ToolStripStatusLabel _lblStatus, _lblCount, _lblHotkeys;
    private StatusKind _statusKind = StatusKind.Info;

    private AppState _state = AppState.Idle;
    private bool _failSafeTriggered;

    public MainForm()
    {
        UiTheme.SetDark(MacroStore.ReadThemeDark());
        Text = "宏连点器 · Macro Clicker";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1240, 700);
        MinimumSize = new Size(1120, 620);
        Font = UiTheme.BaseFont;
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
        _btnDelete = MkBtn("删除宏", AppVariant.Ghost);
        _btnTheme = MkBtn(UiTheme.Dark ? "☀ 浅色" : "🌙 深色", AppVariant.Ghost);
        _btnTheme.Margin = new Padding(8, 3, 2, 3);

        var tt = new ToolTip();
        tt.SetToolTip(_btnRecord, "开始录制 (F6)");
        tt.SetToolTip(_btnStopRec, "停止录制 (F7)");
        tt.SetToolTip(_btnPlay, "开始执行 (F8)");
        tt.SetToolTip(_btnPause, "暂停 / 继续 (F9)");
        tt.SetToolTip(_btnStopPlay, "停止一切执行与录制 (F10)");
        tt.SetToolTip(_btnSave, "保存当前宏（名称取下拉框文本）");
        tt.SetToolTip(_btnDelete, "删除下拉框选中的宏文件");

        _cmbMacro = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Width = 190,
            Margin = new Padding(10, 5, 2, 5),
            IntegralHeight = false
        };
        var macroWrap = UiTheme.Wrap(_cmbMacro);
        macroWrap.Margin = new Padding(10, 8, 2, 8);
        _cmbMacro.TextUpdate += (s, e) => AutoCompleteMacroName();
        _cmbMacro.SelectedIndexChanged += (s, e) => OnMacroSelected();
        _cmbMacro.KeyDown += (s, e) =>
        {
            // 输入已有宏名后回车 = 直接加载
            if (e.KeyCode == Keys.Enter)
            {
                var path = MacroStore.PathOf(Target, _cmbMacro.Text.Trim());
                if (File.Exists(path) && _cmbMacro.SelectedIndex < 0)
                {
                    var idx = _cmbMacro.Items.IndexOf(_cmbMacro.Text.Trim());
                    if (idx >= 0) _cmbMacro.SelectedIndex = idx;
                }
                e.Handled = true;
            }
        };

        toolbar.Controls.AddRange(new Control[]
        {
            _btnRecord, _btnStopRec, _btnPlay, _btnPause, _btnStopPlay,
            new Panel { Width = 2, Height = 26, Margin = new Padding(6, 9, 6, 9), BackColor = UiTheme.C.Divider },
            new Label { Text = "宏:", AutoSize = true, Margin = new Padding(2, 14, 2, 0) },
            macroWrap, _btnSave, _btnDelete,
            new Panel { Width = 2, Height = 26, Margin = new Padding(6, 9, 6, 9), BackColor = UiTheme.C.Divider },
            _btnTheme
        });

        // ---------- 模拟器连接条（仅模拟器页显示） ----------
        _emuStrip = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(10, 10, 10, 4),
            WrapContents = false,
            Visible = false
        };
        _lblDevice = new Label
        {
            Text = "未连接 · 点击「检测设备」",
            AutoSize = false,
            Size = new Size(330, 26),
            Margin = new Padding(2, 9, 8, 0),
            Tag = "sub",
            TextAlign = ContentAlignment.MiddleLeft
        };
        _cmbDevice = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Width = 330,
            Margin = new Padding(2, 5, 2, 5),
            IntegralHeight = false
        };
        var deviceWrap = UiTheme.Wrap(_cmbDevice);
        deviceWrap.Margin = new Padding(2, 8, 2, 8);
        _btnRefresh = MkBtn("⟳ 检测设备", AppVariant.Neutral);
        _btnConnect = MkBtn("连接", AppVariant.Primary);
        _btnDisconnect = MkBtn("断开", AppVariant.Ghost);
        tt.SetToolTip(_cmbDevice, "下拉选择检测到的设备，或直接输入自定义 serial（如 127.0.0.1:5555）");
        tt.SetToolTip(_btnRefresh, "自动发现 adb 与常见模拟器（MuMu/雷电/夜神/逍遥/蓝叠等）");
        _emuStrip.Controls.AddRange(new Control[] { _lblDevice, deviceWrap, _btnRefresh, _btnConnect, _btnDisconnect });

        _btnRefresh.Click += (s, e) => DiscoverDevices();
        _btnConnect.Click += (s, e) => ConnectDevice();
        _btnDisconnect.Click += (s, e) => DisconnectDevice();

        // ---------- 事件列表 ×2 ----------
        _lvWin = MkList();
        _lvEmu = MkList();
        var tabWin = new TabPage("🖥 本机 Windows") { Padding = new Padding(0, 8, 0, 0) };
        var tabEmu = new TabPage("📱 模拟器 (ADB)") { Padding = new Padding(0, 8, 0, 0) };
        tabEmu.Controls.Add(_lvEmu);
        tabEmu.Controls.Add(_emuStrip);
        tabWin.Controls.Add(_lvWin);

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill
        };
        _tabs.TabPages.Add(tabWin);
        _tabs.TabPages.Add(tabEmu);
        _tabs.SelectedIndexChanged += (s, e) => OnTabChanged();

        // ---------- 右侧设置面板（作用于当前页面） ----------
        var right = new Panel
        {
            Dock = DockStyle.Right,
            Width = 320,
            Padding = new Padding(10, 12, 12, 10),
            AutoScroll = true
        };

        _gRec = new AppCard("录制选项") { Dock = DockStyle.Top, Height = 168 };
        int ry = _gRec.ContentTop + 4;
        CheckBox AddCk(string text)
        {
            var ck = new CheckBox { Text = text, AutoSize = true, Location = new Point(16, ry) };
            _gRec.Controls.Add(ck);
            ry += 30;
            return ck;
        }
        _ckKeys = AddCk("记录键盘输入（按键 / 组合键）");
        _ckClicks = AddCk("记录鼠标点击（左/右/中/侧键）");
        _ckWheel = AddCk("记录滚轮（页面滚动）");
        _ckDrags = AddCk("记录拖拽（按住并移动）");

        _gPlay = new AppCard("执行设置") { Dock = DockStyle.Top, Height = 308, Padding = new Padding(10, 6, 10, 6) };
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
            Items = { "执行一次", "执行指定次数", "无限循环" },
            IntegralHeight = false
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
            Items = { "0.25x", "0.5x", "1x", "2x", "4x", "8x" },
            IntegralHeight = false
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
            Text = "提示：每行的“间隔”表示执行该行之前等待的\n时间；回放时该时间会除以播放速度。",
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

        // Fill 先加入，再依次加入边缘停靠控件
        Controls.Add(_tabs);
        Controls.Add(right);
        Controls.Add(toolbar);
        Controls.Add(status);

        // ---------- 事件绑定 ----------
        _recorder.EventRecorded += ev =>
        {
            ActiveEvents.Add(ev);
            var lv = ActiveList;
            lv.Items.Add(MakeItem(ev, ActiveEvents.Count - 1));
            _lblCount.Text = $"事件 {ActiveEvents.Count}";
        };
        _recorder.Warn += msg => Ui(() => SetStatus("⚠ " + msg, StatusKind.Warn));

        _player.Status += s => Ui(() => SetStatus(s, s.StartsWith("⚠") ? StatusKind.Warn : StatusKind.Good));
        _player.AbortedByFailSafe += () => { _failSafeTriggered = true; };
        _player.Finished += ok => Ui(() =>
        {
            var fs = _failSafeTriggered;
            _failSafeTriggered = false;
            var reason = _player.StopReason;
            _player.StopReason = null;
            SetState(AppState.Idle);
            SetStatus(fs ? "⛔ 已触发紧急停止（鼠标左上角）"
                     : !ok && reason != null ? reason
                     : ok ? "✔ 执行完成" : "■ 已停止",
                fs || !ok ? StatusKind.Bad : StatusKind.Good);
        });

        _btnRecord.Click += (s, e) => StartRecording();
        _btnStopRec.Click += (s, e) => StopRecording(true);
        _btnPlay.Click += (s, e) => StartPlayback();
        _btnPause.Click += (s, e) => TogglePause();
        _btnStopPlay.Click += (s, e) => StopAll();
        _btnSave.Click += (s, e) => SaveMacro();
        _btnDelete.Click += (s, e) => DeleteMacro();
        _btnTheme.Click += (s, e) => UiTheme.SetDark(!UiTheme.Dark);

        UiTheme.Changed += OnThemeChanged;
        UiTheme.Apply(this);
        MacroStore.EnsureMigrated();
        RefreshStatus();
        LoadSettings();
        LoadSettingsToUI(MacroTarget.Windows);
        RefreshMacroCombo();
        DiscoverDevices(); // 后台预检测，进入模拟器页即可选择
        SetState(AppState.Idle);
        RebuildList();
    }

    // ================= 页面 / 数据路由 =================

    private MacroTarget Target => _tabs.SelectedIndex == 1 ? MacroTarget.Emulator : MacroTarget.Windows;

    private List<MacroEvent> ActiveEvents => Target == MacroTarget.Emulator ? _emuEvents : _winEvents;

    private AppListView ActiveList => Target == MacroTarget.Emulator ? _lvEmu : _lvWin;

    private MacroStore.TargetSettings CurSettings => Target == MacroTarget.Emulator ? _emuSettings : _winSettings;

    private AppListView MkList()
    {
        var lv = new AppListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = true
        };
        lv.Columns.Add("#", 46);
        lv.Columns.Add("操作", 240);
        lv.Columns.Add("间隔(秒)", 90);
        lv.Columns.Add("参数", 230);
        lv.Columns[3].Width = -2;
        UiTheme.StyleList(lv);
        lv.ContextMenuStrip = BuildMenu();
        lv.DoubleClick += (s, e) => EditSelected();
        lv.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
        };
        return lv;
    }

    private void OnTabChanged()
    {
        if (_state == AppState.Recording) StopRecording(false);
        SnapshotSettingsTo(_curTarget);
        _curTarget = Target;
        LoadSettingsToUI(Target);
        RefreshMacroCombo();
        _emuStrip.Visible = Target == MacroTarget.Emulator;
        RebuildList();
        UpdateDeviceButtons();
        if (Target == MacroTarget.Emulator && _emu != null)
            SetDeviceStatus("已连接 " + _emu.Describe(), true);
    }

    private void OnThemeChanged()
    {
        UiTheme.Apply(this);
        if (_menu != null) UiTheme.StyleMenu(_menu);
        _toolbar.BackColor = UiTheme.C.Panel;
        _emuStrip.BackColor = UiTheme.C.Panel;
        _btnTheme.Text = UiTheme.Dark ? "☀ 浅色" : "🌙 深色";
        if (_emu != null) SetDeviceStatus("已连接 " + _emu.Describe(), true);
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
        Padding = new Padding(11, 5, 11, 5),
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
        _btnRecord.Enabled = idle && (Target != MacroTarget.Emulator || (_emu != null && _emu.IsReady));
        _btnStopRec.Enabled = s == AppState.Recording;
        _btnPlay.Enabled = idle && !(Target == MacroTarget.Emulator && _emu == null);
        _btnPause.Enabled = s is AppState.Playing or AppState.Paused;
        _btnPause.Text = s == AppState.Paused ? "⏵ 继续" : "⏸ 暂停";
        _btnPause.Variant = s == AppState.Paused ? AppVariant.Success : AppVariant.Neutral;
        _btnStopPlay.Enabled = s is AppState.Playing or AppState.Paused or AppState.Recording;
        _btnSave.Enabled = idle;
        _btnDelete.Enabled = idle;
        _gRec.Enabled = idle;
        _gPlay.Enabled = idle;
        _numCount.Enabled = idle && _cmbMode.SelectedIndex == 1;
        _cmbMacro.Enabled = idle;
        UpdateDeviceButtons();
        if (idle)
        {
            _lblCount.Text = $"事件 {ActiveEvents.Count}";
        }
    }

    // ================= 录制 =================

    private void StartRecording()
    {
        if (_state is AppState.Recording or AppState.Playing or AppState.Paused) return;
        if (ActiveEvents.Count > 0 &&
            MessageBox.Show("开始新录制会清空当前事件列表，继续吗？", "确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var opts = new RecordOptions
        {
            RecordKeyboard = _ckKeys.Checked,
            RecordMouseClicks = _ckClicks.Checked,
            RecordWheel = _ckWheel.Checked,
            RecordDrags = _ckDrags.Checked
        };
        if (Target == MacroTarget.Emulator)
        {
            if (_emu == null || !_emu.IsReady)
            {
                _emu?.RefreshInstance();
                if (_emu == null || !_emu.IsReady)
                {
                    SetStatus("请先在上方连接模拟器，再开始录制", StatusKind.Bad);
                    return;
                }
            }
            opts.EmulatorMode = true;
            opts.Session = _emu;
        }

        ActiveEvents.Clear();
        RebuildList();
        _recorder.OwnWindow = Handle;
        try { _recorder.Start(opts); }
        catch (Exception ex)
        {
            MessageBox.Show("启动录制失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        SetState(AppState.Recording);
        SetStatus(Target == MacroTarget.Emulator
            ? "● 模拟器录制中… 在模拟器窗口内点击/滑动/滚动即可（按 F7 结束）"
            : "● 录制中…（按 F7 或点击“停止录制”结束）", StatusKind.Bad);
    }

    private void StopRecording(bool notify)
    {
        if (!_recorder.IsRecording) return;
        _recorder.Stop();
        SetState(AppState.Idle);
        SetStatus(notify ? $"✔ 录制完成，共 {ActiveEvents.Count} 个事件" : "■ 录制已停止", StatusKind.Info);
    }

    // ================= 执行 =================

    private void StartPlayback()
    {
        if (_state is AppState.Playing or AppState.Paused or AppState.Recording) return;
        if (ActiveEvents.Count == 0)
        {
            SetStatus("没有可执行的事件，请先录制或从「宏」下拉选择", StatusKind.Bad);
            return;
        }
        if (Target == MacroTarget.Emulator && (_emu == null || !_emu.IsReady))
        {
            SetStatus("请先连接模拟器，再执行", StatusKind.Bad);
            return;
        }

        SnapshotSettingsTo(Target);
        double speed = 1.0;
        var speedText = _cmbSpeed.Text.TrimEnd('x', 'X', ' ');
        if (double.TryParse(speedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0) speed = v;

        var s = CurSettings;
        var settings = new PlaySettings
        {
            Mode = (LoopMode)s.LoopMode,
            Count = s.LoopCount,
            LoopInterval = s.LoopInterval,
            Speed = speed,
            CountdownSeconds = s.Countdown,
            FailSafe = _ckFailsafe.Checked
        };
        _failSafeTriggered = false;
        var snapshot = new List<MacroEvent>(ActiveEvents);
        SetState(AppState.Playing);
        SetStatus(Target == MacroTarget.Emulator ? "▶ 开始执行（模拟器 · 不占鼠标）…" : "▶ 开始执行…", StatusKind.Good);
        _player.Start(snapshot, settings, Target == MacroTarget.Emulator ? _emu : null);
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
        if (_recorder.IsRecording) StopRecording(false);
        if (_state is AppState.Playing or AppState.Paused || _player.IsBusy) _player.Stop();
    }

    // ================= 列表操作 =================

    private static ListViewItem MakeItem(MacroEvent e, int idx)
    {
        var it = new ListViewItem((idx + 1).ToString()) { Tag = e };
        it.SubItems.Add(e.Display);
        it.SubItems.Add(e.Delay.ToString("0.###"));
        it.SubItems.Add(e.Params);
        return it;
    }

    private void RebuildList()
    {
        var lv = ActiveList;
        var events = ActiveEvents;
        lv.BeginUpdate();
        lv.Items.Clear();
        for (int i = 0; i < events.Count; i++) lv.Items.Add(MakeItem(events[i], i));
        lv.EndUpdate();
        _lblCount.Text = $"事件 {events.Count}";
    }

    private void SelectIndex(int i)
    {
        var lv = ActiveList;
        if (i >= 0 && i < lv.Items.Count)
        {
            lv.Items[i].Selected = true;
            lv.Items[i].EnsureVisible();
        }
    }

    private void EditSelected()
    {
        if (_state != AppState.Idle) return;
        var lv = ActiveList;
        if (lv.SelectedIndices.Count != 1) return;
        int i = lv.SelectedIndices[0];
        using var dlg = new EventEditForm(ActiveEvents[i]);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            RebuildList();
            SelectIndex(i);
        }
    }

    private void DeleteSelected()
    {
        if (_state != AppState.Idle) return;
        var lv = ActiveList;
        if (lv.SelectedIndices.Count == 0) return;
        foreach (int i in lv.SelectedIndices.Cast<int>().OrderByDescending(x => x))
            ActiveEvents.RemoveAt(i);
        RebuildList();
    }

    private void MoveSelected(int delta)
    {
        if (_state != AppState.Idle) return;
        var lv = ActiveList;
        if (lv.SelectedIndices.Count != 1) return;
        int i = lv.SelectedIndices[0];
        int j = i + delta;
        if (j < 0 || j >= ActiveEvents.Count) return;
        (ActiveEvents[i], ActiveEvents[j]) = (ActiveEvents[j], ActiveEvents[i]);
        RebuildList();
        SelectIndex(j);
    }

    private void CopySelected()
    {
        if (_state != AppState.Idle) return;
        var lv = ActiveList;
        if (lv.SelectedIndices.Count != 1) return;
        int i = lv.SelectedIndices[0];
        ActiveEvents.Insert(i + 1, ActiveEvents[i].Clone());
        RebuildList();
        SelectIndex(i + 1);
    }

    private void InsertEvent(MacroEvent ev)
    {
        if (_state != AppState.Idle) return;
        var lv = ActiveList;
        int i = lv.SelectedIndices.Count == 1 ? lv.SelectedIndices[0] + 1 : ActiveEvents.Count;
        if (ev.Type == EventType.MouseClick && Target == MacroTarget.Emulator)
        {
            ev.CoordSpace = "device";
            if (_emu != null) { ev.X = _emu.Device.Width / 2; ev.Y = _emu.Device.Height / 2; }
        }
        if (ev.Type == EventType.Swipe && Target == MacroTarget.Emulator)
        {
            ev.CoordSpace = "device";
            if (_emu != null)
            {
                ev.X = ev.X2 = _emu.Device.Width / 2;
                ev.Y = _emu.Device.Height / 3;
                ev.Y2 = _emu.Device.Height * 2 / 3;
            }
        }
        using var dlg = new EventEditForm(ev);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            ActiveEvents.Insert(i, ev);
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
        var insSwipe = new ToolStripMenuItem("插入滑动 / 长按…");
        var insKey = new ToolStripMenuItem("插入按键…");
        var insWait = new ToolStripMenuItem("插入等待…");

        edit.Click += (s, e) => EditSelected();
        del.Click += (s, e) => DeleteSelected();
        up.Click += (s, e) => MoveSelected(-1);
        down.Click += (s, e) => MoveSelected(1);
        copy.Click += (s, e) => CopySelected();
        insClick.Click += (s, e) =>
        {
            if (Target == MacroTarget.Windows)
            {
                Win32.GetCursorPos(out var p);
                InsertEvent(new MacroEvent { Type = EventType.MouseClick, X = p.X, Y = p.Y, Delay = 0 });
            }
            else
            {
                InsertEvent(new MacroEvent { Type = EventType.MouseClick, CoordSpace = "device", Delay = 0 });
            }
        };
        insSwipe.Click += (s, e) =>
            InsertEvent(new MacroEvent { Type = EventType.Swipe, CoordSpace = "device", DurationMs = 300, Delay = 0 });
        insKey.Click += (s, e) =>
            InsertEvent(new MacroEvent { Type = EventType.Key, Vk = 0x0D, Delay = 0 });
        insWait.Click += (s, e) =>
            InsertEvent(new MacroEvent { Type = EventType.Wait, Delay = 1 });

        m.Items.AddRange(new ToolStripItem[]
        {
            edit, del, new ToolStripSeparator(), up, down, copy,
            new ToolStripSeparator(), insClick, insSwipe, insKey, insWait
        });

        m.Opening += (s, e) =>
        {
            bool idle = _state == AppState.Idle;
            bool one = idle && ActiveList.SelectedIndices.Count == 1;
            bool any = idle && ActiveList.SelectedIndices.Count > 0;
            edit.Enabled = one;
            del.Enabled = any;
            up.Enabled = one;
            down.Enabled = one;
            copy.Enabled = one;
            insClick.Enabled = insKey.Enabled = insWait.Enabled = idle;
            insSwipe.Visible = Target == MacroTarget.Emulator;
            insSwipe.Enabled = idle;
            insClick.Text = Target == MacroTarget.Emulator ? "插入点击(设备)…" : "插入鼠标点击…";
        };
        UiTheme.StyleMenu(m);
        return m;
    }

    // ================= 模拟器（ADB） =================

    private void SetDeviceStatus(string text, bool good)
    {
        _lblDevice.Text = text;
        _lblDevice.ForeColor = good ? UiTheme.C.Success : UiTheme.C.SubText;
    }

    /// <summary>后台发现 adb 与在线设备，填充下拉。</summary>
    private void DiscoverDevices()
    {
        if (_discovering) return;
        _discovering = true;
        _btnRefresh.Enabled = false;
        SetDeviceStatus("正在检测 adb 与模拟器…", false);
        Task.Run(() =>
        {
            var (adb, devices, error) = EmulatorScanner.Discover();
            Ui(() =>
            {
                _discovering = false;
                _btnRefresh.Enabled = true;
                _adb = adb;
                _emuCandidates = devices;
                _mumuRequery = devices.FirstOrDefault()?.MumuRequery;
                _cmbDevice.Items.Clear();
                foreach (var d in devices)
                    _cmbDevice.Items.Add(d.Display);
                if (_emu != null)
                {
                    SetDeviceStatus(_emu.IsReady ? "已连接 " + _emu.Describe() : "设备似乎已离线，请重新连接", _emu.IsReady);
                }
                else if (devices.Count > 0)
                {
                    // 恢复上次选择的 serial
                    int sel = -1;
                    if (!string.IsNullOrEmpty(_settingsEmuSerial))
                        sel = devices.FindIndex(d => d.Serial == _settingsEmuSerial);
                    if (sel < 0) sel = 0;
                    _cmbDevice.SelectedIndex = sel;
                    SetDeviceStatus($"检测到 {devices.Count} 台设备 · 点「连接」", false);
                }
                else
                {
                    SetDeviceStatus(error, false);
                }
                UpdateDeviceButtons();
                SetState(_state); // 刷新录制按钮可用性
            });
        });
    }

    private string? _settingsEmuSerial;

    /// <summary>连接下拉选择（或手动输入）的设备。</summary>
    private void ConnectDevice()
    {
        if (_adb == null)
        {
            SetDeviceStatus("尚未检测到 adb，先点「检测设备」", false);
            DiscoverDevices();
            return;
        }
        var text = _cmbDevice.Text.Trim();
        if (text.Length == 0)
        {
            SetDeviceStatus("请先选择或输入设备（如 127.0.0.1:5555）", false);
            return;
        }
        string serial = text;
        var candidate = _emuCandidates.FirstOrDefault(c => c.Display == text);
        if (candidate != null)
        {
            serial = candidate.Serial;
        }
        else if (text.Contains('·'))
        {
            serial = text.Split('·')[1].Trim(); // 显示文本形如 "家族 · 127.0.0.1:16384 · 分辨率"
        }
        else if (int.TryParse(text, out int port))
        {
            serial = $"127.0.0.1:{port}";
        }

        SetDeviceStatus($"正在连接 {serial} …", false);
        var family = candidate?.Family ?? "自定义设备";
        var mumu = candidate?.Mumu;
        var requery = _mumuRequery;
        Task.Run(() =>
        {
            var session = new EmulatorSession(_adb!, serial, family, mumu, requery);
            var error = session.Connect();
            Ui(() =>
            {
                if (error.Length > 0)
                {
                    SetDeviceStatus($"连接 {serial} 失败：{error}", false);
                    return;
                }
                _emu = session;
                _settingsEmuSerial = serial;
                SetDeviceStatus("已连接 " + session.Describe(), true);
                UpdateDeviceButtons();
                SetState(_state);
            });
        });
    }

    private void DisconnectDevice()
    {
        _emu = null;
        SetDeviceStatus("已断开连接", false);
        UpdateDeviceButtons();
        SetState(_state);
    }

    private void UpdateDeviceButtons()
    {
        var idle = _state == AppState.Idle;
        _btnConnect.Enabled = idle && _adb != null;
        _btnDisconnect.Enabled = idle && _emu != null;
        _cmbDevice.Enabled = idle;
        _btnRefresh.Enabled = idle && !_discovering;
    }

    // ================= 宏下拉 / 保存 / 删除 =================

    private void RefreshMacroCombo()
    {
        _loadingMacro = true;
        _cmbMacro.Items.Clear();
        foreach (var (name, _) in MacroStore.ListMacros(Target))
            _cmbMacro.Items.Add(name);
        _cmbMacro.Text = "";
        _cmbMacro.SelectedIndex = -1;
        _loadingMacro = false;
    }

    private bool _loadingMacro;

    private void OnMacroSelected()
    {
        if (_loadingMacro || _cmbMacro.SelectedIndex < 0) return;
        string name = _cmbMacro.Text;
        var path = MacroStore.PathOf(Target, name);
        if (!File.Exists(path)) return;
        if (ActiveEvents.Count > 0 &&
            MessageBox.Show($"加载宏「{name}」会替换当前 {ActiveEvents.Count} 个事件，继续吗？", "确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            RefreshMacroCombo();
            return;
        }
        try
        {
            var (macroName, list) = MacroStore.Load(path);
            ActiveEvents.Clear();
            ActiveEvents.AddRange(list);
            RebuildList();
            SetStatus($"✔ 已加载宏「{macroName}」· {list.Count} 个事件", StatusKind.Good);
        }
        catch (Exception ex)
        {
            MessageBox.Show("加载失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>下拉框输入时联想已有宏名（不强制），回车可加载。</summary>
    private void AutoCompleteMacroName()
    {
        var text = _cmbMacro.Text;
        if (text.Length == 0) return;
        var hit = _cmbMacro.Items.Cast<string>()
            .FirstOrDefault(n => n.StartsWith(text, StringComparison.OrdinalIgnoreCase));
        if (hit == null || hit == text) return;
        int start = text.Length;
        _cmbMacro.Text = hit;
        _cmbMacro.SelectionStart = start;
        _cmbMacro.SelectionLength = hit.Length - start;
    }

    private void SaveMacro()
    {
        if (_state != AppState.Idle) return;
        var name = _cmbMacro.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show("请先在「宏」下拉框中输入或选择一个名称。", "提示");
            _cmbMacro.Focus();
            return;
        }
        if (ActiveEvents.Count == 0)
        {
            MessageBox.Show("没有事件可保存。");
            return;
        }
        try
        {
            var path = MacroStore.PathOf(Target, name);
            MacroStore.Save(path, name, Target, ActiveEvents);
            RefreshMacroCombo();
            _cmbMacro.Text = name;
            SetStatus($"✔ 已保存 {ActiveEvents.Count} 个事件 → 宏「{name}」", StatusKind.Good);
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DeleteMacro()
    {
        if (_state != AppState.Idle) return;
        var name = _cmbMacro.Text.Trim();
        var path = MacroStore.PathOf(Target, name);
        if (name.Length == 0 || !File.Exists(path))
        {
            SetStatus("当前名称不是已保存的宏", StatusKind.Warn);
            return;
        }
        if (MessageBox.Show($"确定删除宏「{name}」？", "确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        MacroStore.DeleteMacro(path);
        RefreshMacroCombo();
        SetStatus($"已删除宏「{name}」", StatusKind.Info);
    }

    // ================= 设置持久化 =================

    private void SnapshotSettingsTo(MacroTarget target)
    {
        var s = target == MacroTarget.Emulator ? _emuSettings : _winSettings;
        s.LoopMode = _cmbMode.SelectedIndex;
        s.LoopCount = (int)_numCount.Value;
        s.LoopInterval = (double)_numInterval.Value;
        s.Speed = _cmbSpeed.Text;
        s.Countdown = (int)_numCountdown.Value;
        s.FailSafe = _ckFailsafe.Checked;
        s.RecKeys = _ckKeys.Checked;
        s.RecClicks = _ckClicks.Checked;
        s.RecWheel = _ckWheel.Checked;
        s.RecDrags = _ckDrags.Checked;
    }

    private void LoadSettingsToUI(MacroTarget target)
    {
        var s = target == MacroTarget.Emulator ? _emuSettings : _winSettings;
        _cmbMode.SelectedIndex = Math.Clamp(s.LoopMode, 0, 2);
        _numCount.Value = Math.Clamp(s.LoopCount, 1, 999999);
        _numInterval.Value = (decimal)Math.Clamp(s.LoopInterval, 0.0, 3600.0);
        var si = _cmbSpeed.Items.IndexOf(s.Speed ?? "1x");
        _cmbSpeed.SelectedIndex = si >= 0 ? si : 2;
        _numCountdown.Value = Math.Clamp(s.Countdown, 0, 10);
        _ckFailsafe.Checked = s.FailSafe;
        _ckKeys.Checked = s.RecKeys;
        _ckClicks.Checked = s.RecClicks;
        _ckWheel.Checked = s.RecWheel;
        _ckDrags.Checked = s.RecDrags;
        _numCount.Enabled = _cmbMode.SelectedIndex == 1;
    }

    private void SaveSettings()
    {
        try
        {
            SnapshotSettingsTo(Target);
            var dto = new MacroStore.AppSettings
            {
                Theme = UiTheme.Dark ? "dark" : "light",
                Win = _winSettings,
                Emu = _emuSettings,
                EmuSerial = _settingsEmuSerial,
                WinW = WindowState == FormWindowState.Normal ? Size.Width : RestoreBounds.Width,
                WinH = WindowState == FormWindowState.Normal ? Size.Height : RestoreBounds.Height,
                WinMax = WindowState == FormWindowState.Maximized
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
            if (dto.Win != null) _winSettings = dto.Win;
            if (dto.Emu != null) _emuSettings = dto.Emu;
            _settingsEmuSerial = dto.EmuSerial;
            if (dto.Win == null) LoadLegacySettings(p);
            if (dto.WinW >= MinimumSize.Width && dto.WinH >= MinimumSize.Height)
            {
                Size = new Size(Math.Min(dto.WinW, Screen.PrimaryScreen!.WorkingArea.Width),
                                Math.Min(dto.WinH, Screen.PrimaryScreen.WorkingArea.Height));
                if (dto.WinMax) WindowState = FormWindowState.Maximized;
            }
        }
        catch { }
    }

    /// <summary>旧版（单目标扁平字段）config.json → Windows 页设置迁移。</summary>
    private void LoadLegacySettings(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var s = _winSettings;
            if (r.TryGetProperty("LoopMode", out var v1)) s.LoopMode = v1.GetInt32();
            if (r.TryGetProperty("LoopCount", out var v2)) s.LoopCount = v2.GetInt32();
            if (r.TryGetProperty("LoopInterval", out var v3)) s.LoopInterval = v3.GetDouble();
            if (r.TryGetProperty("Speed", out var v4) && v4.ValueKind == JsonValueKind.String) s.Speed = v4.GetString() ?? "1x";
            if (r.TryGetProperty("Countdown", out var v5)) s.Countdown = v5.GetInt32();
            if (r.TryGetProperty("FailSafe", out var v6)) s.FailSafe = v6.GetBoolean();
            if (r.TryGetProperty("RecKeys", out var v7)) s.RecKeys = v7.GetBoolean();
            if (r.TryGetProperty("RecClicks", out var v8)) s.RecClicks = v8.GetBoolean();
            if (r.TryGetProperty("RecWheel", out var v9)) s.RecWheel = v9.GetBoolean();
            if (r.TryGetProperty("RecDrags", out var v10)) s.RecDrags = v10.GetBoolean();
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
                    if (_recorder.IsRecording) StopRecording(true);
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

    /// <summary>Aero 贴靠（Win+方向键）不受 MinimumSize 约束，这里把过小尺寸钳回下限，保证布局完整。</summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Normal && (Width < MinimumSize.Width || Height < MinimumSize.Height))
            Size = new Size(Math.Max(Width, MinimumSize.Width), Math.Max(Height, MinimumSize.Height));
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
