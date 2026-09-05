using System.Diagnostics;

namespace MacroClicker;

internal sealed class RecordOptions
{
    public bool RecordKeyboard = true;
    public bool RecordMouseClicks = true;
    public bool RecordWheel = true;
    public bool RecordDrags = true;

    /// <summary>模拟器录制模式：仅记录落在模拟器窗口内的动作，并把屏幕坐标换算为设备像素。</summary>
    public bool EmulatorMode;
    public Emulator.EmulatorSession? Session;
}

/// <summary>
/// 录制器：把全局输入流整理成语义化事件（点击/组合键/拖拽/滚轮），
/// 并为每个事件记录与上一事件的时间间隔（delta time）。
/// 模拟器模式下把 点击/长按/滑动/滚轮(页面滚动)/按键 录制为设备坐标事件。
/// </summary>
internal sealed class Recorder : IDisposable
{
    private const int ClickSlopPx = 8;        // 位移不超过该值视为点击/长按
    private const int LongPressMs = 500;      // 按住超过该时长视为长按
    private const int MaxSwipeMs = 60000;     // input swipe 时长上限

    private readonly GlobalHook _hook = new();
    private readonly Stopwatch _sw = new();

    private readonly List<uint> _held = new();             // 当前按住的键（按下顺序）
    private readonly HashSet<uint> _consumedMods = new();  // 已并入组合键/带修饰键动作的修饰键

    private string? _downBtn;                              // 当前按住的鼠标键
    private int _downX, _downY;
    private double _downT;
    private bool _dragFlushed;
    private int _lastDragX, _lastDragY;
    private double _lastT;

    // 模拟器模式的手势状态
    private bool _emuDownValid;
    private int _emuDownX, _emuDownY, _emuCurX, _emuCurY;
    private double _emuDownT;

    public bool IsRecording { get; private set; }
    public RecordOptions Options { get; private set; } = new();

    /// <summary>主窗口句柄：录制时忽略发生在本程序窗口内的点击/按键。</summary>
    public IntPtr OwnWindow { get; set; }

    public event Action<MacroEvent>? EventRecorded;

    /// <summary>出现无法录制的动作（模拟器模式右键/组合键等）时提示。</summary>
    public event Action<string>? Warn;

    private readonly HashSet<string> _warned = new();

    public Recorder()
    {
        _hook.KeyEvent += OnKeyEvent;
        _hook.MouseEvent += OnMouseEvent;
        _hook.WheelEvent += OnWheelEvent;
    }

    public void Start(RecordOptions options)
    {
        Options = options;
        _held.Clear();
        _consumedMods.Clear();
        _downBtn = null;
        _dragFlushed = false;
        _lastT = 0;
        _emuDownValid = false;
        _warned.Clear();
        _sw.Restart();
        _hook.Install();
        IsRecording = true;
    }

    public void Stop()
    {
        if (!IsRecording) return;
        IsRecording = false;
        _hook.Uninstall();
    }

    public void Dispose() => _hook.Dispose();

    private void Emit(MacroEvent ev, double t)
    {
        ev.Delay = Math.Max(0, t - _lastT);
        _lastT = Math.Max(_lastT, t);
        EventRecorded?.Invoke(ev);
    }

    private void WarnOnce(string key, string msg)
    {
        if (_warned.Add(key)) Warn?.Invoke(msg);
    }

    private bool OwnWindowIsForeground() => OwnWindow != IntPtr.Zero && Win32.GetForegroundWindow() == OwnWindow;

    private bool PointOnOwnWindow(int x, int y) =>
        OwnWindow != IntPtr.Zero &&
        Win32.GetAncestor(Win32.WindowFromPoint(new Win32.POINT { X = x, Y = y }), 2) == OwnWindow;

    // ---------------- 键盘 ----------------

    private void OnKeyEvent(uint vk, bool down)
    {
        if (!IsRecording || !Options.RecordKeyboard) return;
        if (vk is >= 0x75 and <= 0x79) return;                 // F6–F10 是程序保留热键，永不录制
        if (OwnWindowIsForeground()) return;                   // 用户正在操作本程序窗口

        if (Options.EmulatorMode)
        {
            EmuKey(vk, down);
            return;
        }

        double t = _sw.Elapsed.TotalSeconds;
        if (down)
        {
            if (!_held.Contains(vk)) _held.Add(vk);
            return;
        }

        if (!_held.Remove(vk)) return;

        if (KeyMap.IsModifier(vk))
        {
            // 修饰键松开时若尚未被任何组合键消费，说明用户单独按了一下（如单按 Ctrl）
            if (_consumedMods.Remove(vk)) return;
            Emit(new MacroEvent { Type = EventType.Key, Vk = vk }, t);
            return;
        }

        // 松开的是普通键：按住期间还有哪些修饰键，就组成组合键（如 ctrl+c、shift+a）
        var mods = _held.Where(KeyMap.IsModifier).ToList();
        if (mods.Count > 0)
        {
            var combo = new List<uint>(mods) { vk };
            Emit(new MacroEvent { Type = EventType.Hotkey, Combo = combo }, t);
            foreach (var m in mods) _consumedMods.Add(m);
        }
        else
        {
            Emit(new MacroEvent { Type = EventType.Key, Vk = vk }, t);
        }
    }

    /// <summary>模拟器模式键盘：仅单键可映射为 Android keycode；组合键提示后忽略。</summary>
    private void EmuKey(uint vk, bool down)
    {
        double t = _sw.Elapsed.TotalSeconds;
        if (down)
        {
            if (!_held.Contains(vk)) _held.Add(vk);
            return;
        }
        if (!_held.Remove(vk)) return;
        if (KeyMap.IsModifier(vk))
        {
            if (_consumedMods.Remove(vk)) return;
            WarnOnce("emumod", "模拟器模式不支持单独的修饰键（Ctrl/Shift/Alt/Win），已忽略");
            return;
        }
        var mods = _held.Where(KeyMap.IsModifier).ToList();
        if (mods.Count > 0)
        {
            foreach (var m in mods) _consumedMods.Add(m);
            WarnOnce("emucombo", "模拟器模式不支持组合键，已忽略");
            return;
        }
        if (Emulator.AndroidKeys.FromName(KeyMap.NameOf(vk)) == 0)
        {
            WarnOnce("emukey" + vk, $"模拟器模式暂不支持按键 {KeyMap.NameOf(vk)}，已跳过");
            return;
        }
        Emit(new MacroEvent { Type = EventType.Key, Vk = vk, CoordSpace = "device" }, t);
    }

    // ---------------- 鼠标 ----------------

    private void OnMouseEvent(int msg, int x, int y, int data)
    {
        if (!IsRecording) return;
        double t = _sw.Elapsed.TotalSeconds;

        switch (msg)
        {
            case Win32.WM_LBUTTONDOWN:
            case Win32.WM_RBUTTONDOWN:
            case Win32.WM_MBUTTONDOWN:
            case Win32.WM_XBUTTONDOWN:
                if (Options.EmulatorMode)
                {
                    EmuDown(msg, x, y, t);
                    return;
                }
                if (!Options.RecordMouseClicks) return;
                _downBtn = ButtonOf(msg, data);
                _downX = x; _downY = y; _downT = t;
                _dragFlushed = false;
                _lastDragX = x; _lastDragY = y;
                return;

            case Win32.WM_LBUTTONUP:
            case Win32.WM_RBUTTONUP:
            case Win32.WM_MBUTTONUP:
            case Win32.WM_XBUTTONUP:
                if (Options.EmulatorMode)
                {
                    EmuUp(msg, x, y, t);
                    return;
                }
                if (!Options.RecordMouseClicks || _downBtn == null) return;
                if (PointOnOwnWindow(x, y)) { _downBtn = null; return; }
                var upMods = _held.Where(KeyMap.IsModifier).ToList();
                if (_dragFlushed)
                    Emit(new MacroEvent { Type = EventType.MouseUp, Button = _downBtn, X = x, Y = y, Modifiers = upMods }, t);
                else
                    Emit(new MacroEvent { Type = EventType.MouseClick, Button = _downBtn, X = _downX, Y = _downY, Modifiers = upMods }, t);
                foreach (var m in upMods) _consumedMods.Add(m);
                _downBtn = null;
                return;

            case Win32.WM_MOUSEMOVE:
                if (Options.EmulatorMode)
                {
                    if (_emuDownValid) { _emuCurX = x; _emuCurY = y; }
                    return;
                }
                if (_downBtn != null)
                {
                    // 按住移动 = 拖拽：位移超过阈值后先把“按下”落盘，再记录轨迹点
                    if (!Options.RecordDrags) return;
                    if (!_dragFlushed)
                    {
                        if (Math.Abs(x - _downX) + Math.Abs(y - _downY) > 6)
                        {
                            _dragFlushed = true;
                            if (!PointOnOwnWindow(_downX, _downY))
                            {
                                var mods = _held.Where(KeyMap.IsModifier).ToList();
                                Emit(new MacroEvent { Type = EventType.MouseDown, Button = _downBtn, X = _downX, Y = _downY, Modifiers = mods }, _downT);
                                foreach (var m in mods) _consumedMods.Add(m);
                            }
                            Emit(new MacroEvent { Type = EventType.MouseMove, X = x, Y = y }, t);
                            _lastDragX = x; _lastDragY = y;
                        }
                    }
                    else if (Math.Abs(x - _lastDragX) + Math.Abs(y - _lastDragY) > 4)
                    {
                        Emit(new MacroEvent { Type = EventType.MouseMove, X = x, Y = y }, t);
                        _lastDragX = x; _lastDragY = y;
                    }
                }
                return;
        }
    }

    // ---------------- 模拟器手势 ----------------

    private void EmuDown(int msg, int x, int y, double t)
    {
        var session = Options.Session;
        if (session == null) return;
        _emuDownValid = false;
        if (!Options.RecordMouseClicks && !Options.RecordDrags) return;
        if (msg != Win32.WM_LBUTTONDOWN)
        {
            WarnOnce("emubtn", "模拟器模式仅录制鼠标左键动作，其他按键已忽略");
            return;
        }
        if (!session.TryMapScreen(x, y, out var dev))
            return; // 手势起落在模拟器窗口之外：忽略整个手势
        _emuDownValid = true;
        _emuDownX = x; _emuDownY = y;
        _emuCurX = x; _emuCurY = y;
        _emuDownT = t;
        _emuDownDevX = dev.X; _emuDownDevY = dev.Y;
    }

    private int _emuDownDevX, _emuDownDevY;

    private void EmuUp(int msg, int x, int y, double t)
    {
        var session = Options.Session;
        if (session == null || !_emuDownValid) return;
        _emuDownValid = false;
        if (msg != Win32.WM_LBUTTONUP) return;

        var devStart = new Point(_emuDownDevX, _emuDownDevY);
        session.TryMapScreen(x, y, out var devEnd); // 越界时钳制到设备内
        int durMs = (int)Math.Clamp((t - _emuDownT) * 1000, 50, MaxSwipeMs);
        int moved = Math.Abs(x - _emuDownX) + Math.Abs(y - _emuDownY);

        if (moved <= ClickSlopPx)
        {
            if (!Options.RecordMouseClicks) return;
            if (durMs < LongPressMs)
            {
                Emit(new MacroEvent { Type = EventType.MouseClick, X = devStart.X, Y = devStart.Y, CoordSpace = "device" }, t);
            }
            else
            {
                // 长按 = 同点滑动
                Emit(new MacroEvent
                {
                    Type = EventType.Swipe, DurationMs = durMs,
                    X = devStart.X, Y = devStart.Y, X2 = devStart.X, Y2 = devStart.Y,
                    CoordSpace = "device"
                }, t);
            }
        }
        else
        {
            if (!Options.RecordDrags)
            {
                WarnOnce("emudrag", "已忽略滑动动作（录制选项未开启「记录滑动/拖动」）");
                return;
            }
            Emit(new MacroEvent
            {
                Type = EventType.Swipe, DurationMs = durMs,
                X = devStart.X, Y = devStart.Y, X2 = devEnd.X, Y2 = devEnd.Y,
                CoordSpace = "device"
            }, t);
        }
    }

    // ---------------- 滚轮 ----------------

    private void OnWheelEvent(int x, int y, int delta)
    {
        if (!IsRecording || !Options.RecordWheel) return;

        if (Options.EmulatorMode)
        {
            var session = Options.Session;
            if (session == null) return;
            if (!session.TryMapScreen(x, y, out var dev)) return;
            // 滚轮 → 页面滚动：一格约滚动 1/8 屏（设备像素），wheel-up 时内容上移 = 手指下移
            int unit = Math.Max(80, session.Device.Height / 8);
            int notches = Math.Max(1, Math.Abs(delta) / 120);
            int dist = Math.Clamp(unit * notches, 80, session.Device.Height);
            int dir = delta > 0 ? 1 : -1;
            int y2 = Math.Clamp(dev.Y + dir * dist, 0, session.Device.Height - 1);
            int durMs = Math.Clamp(150 * notches, 150, 800);
            Emit(new MacroEvent
            {
                Type = EventType.Swipe, DurationMs = durMs,
                X = dev.X, Y = dev.Y, X2 = dev.X, Y2 = y2,
                CoordSpace = "device"
            }, _sw.Elapsed.TotalSeconds);
            return;
        }

        if (PointOnOwnWindow(x, y)) return;
        var mods = _held.Where(KeyMap.IsModifier).ToList();
        Emit(new MacroEvent { Type = EventType.Wheel, X = x, Y = y, Delta = delta, Modifiers = mods }, _sw.Elapsed.TotalSeconds);
        foreach (var m in mods) _consumedMods.Add(m);
    }

    private static string ButtonOf(int msg, int data) => msg switch
    {
        Win32.WM_LBUTTONDOWN or Win32.WM_LBUTTONUP => "left",
        Win32.WM_RBUTTONDOWN or Win32.WM_RBUTTONUP => "right",
        Win32.WM_MBUTTONDOWN or Win32.WM_MBUTTONUP => "middle",
        Win32.WM_XBUTTONDOWN or Win32.WM_XBUTTONUP => data == 2 ? "x2" : "x1",
        _ => "left"
    };
}
