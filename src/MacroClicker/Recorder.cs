using System.Diagnostics;

namespace MacroClicker;

internal sealed class RecordOptions
{
    public bool RecordKeyboard = true;
    public bool RecordMouseClicks = true;
    public bool RecordWheel = true;
    public bool RecordDrags = true;
    public bool RecordMouseMove = false;
}

/// <summary>
/// 录制器：把全局输入流整理成语义化事件（点击/组合键/拖拽/滚轮），
/// 并为每个事件记录与上一事件的时间间隔（delta time）。
/// </summary>
internal sealed class Recorder : IDisposable
{
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
    private double _lastFreeMoveT;

    public bool IsRecording { get; private set; }
    public List<MacroEvent> Events { get; } = new();
    public RecordOptions Options { get; private set; } = new();

    /// <summary>主窗口句柄：录制时忽略发生在本程序窗口内的点击/按键。</summary>
    public IntPtr OwnWindow { get; set; }

    public event Action<MacroEvent>? EventRecorded;

    public Recorder()
    {
        _hook.KeyEvent += OnKeyEvent;
        _hook.MouseEvent += OnMouseEvent;
        _hook.WheelEvent += OnWheelEvent;
    }

    public void Start(RecordOptions options)
    {
        Options = options;
        Events.Clear();
        _held.Clear();
        _consumedMods.Clear();
        _downBtn = null;
        _dragFlushed = false;
        _lastT = 0;
        _lastFreeMoveT = 0;
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
        Events.Add(ev);
        EventRecorded?.Invoke(ev);
    }

    private bool OwnWindowIsForeground() => OwnWindow != IntPtr.Zero && Win32.GetForegroundWindow() == OwnWindow;

    private bool PointOnOwnWindow(int x, int y) =>
        OwnWindow != IntPtr.Zero &&
        Win32.GetAncestor(Win32.WindowFromPoint(new Win32.POINT { X = x, Y = y }), 2) == OwnWindow;

    private void OnKeyEvent(uint vk, bool down)
    {
        if (!IsRecording || !Options.RecordKeyboard) return;
        if (vk is >= 0x75 and <= 0x79) return;                 // F6–F10 是程序保留热键，永不录制
        if (OwnWindowIsForeground()) return;                   // 用户正在操作本程序窗口

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
            {
                if (!Options.RecordMouseClicks || _downBtn == null) return;
                if (PointOnOwnWindow(x, y)) { _downBtn = null; return; }
                var mods = _held.Where(KeyMap.IsModifier).ToList();
                if (_dragFlushed)
                    Emit(new MacroEvent { Type = EventType.MouseUp, Button = _downBtn, X = x, Y = y, Modifiers = mods }, t);
                else
                    Emit(new MacroEvent { Type = EventType.MouseClick, Button = _downBtn, X = _downX, Y = _downY, Modifiers = mods }, t);
                foreach (var m in mods) _consumedMods.Add(m);
                _downBtn = null;
                return;
            }

            case Win32.WM_MOUSEMOVE:
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
                else if (Options.RecordMouseMove && t - _lastFreeMoveT >= 0.02)
                {
                    // 空移动默认关闭；开启时做 20ms 节流
                    Emit(new MacroEvent { Type = EventType.MouseMove, X = x, Y = y }, t);
                    _lastFreeMoveT = t;
                }
                return;
        }
    }

    private void OnWheelEvent(int x, int y, int delta)
    {
        if (!IsRecording || !Options.RecordWheel) return;
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
