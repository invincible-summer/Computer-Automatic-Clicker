using System.Drawing;

namespace MacroClicker.Emulator;

/// <summary>
/// MuMu 模拟器会话：跟踪模拟器渲染窗口矩形（自适应边框/移动/缩放），
/// 在「屏幕坐标 ↔ 安卓设备坐标」之间换算，并通过 ADB 注入点击/滑动/按键——
/// 全程不移动、不占用本机鼠标。
/// </summary>
internal sealed class EmulatorSession
{
    private readonly AdbClient _adb;
    private readonly Func<List<MuMuInstance>> _requery;
    private MuMuInstance _inst;
    private bool _connected;

    public EmulatorSession(AdbClient adb, MuMuInstance inst, Func<List<MuMuInstance>> requery)
    {
        _adb = adb;
        _inst = inst;
        _requery = requery;
    }

    public MuMuInstance Instance => _inst;
    public string Serial => _inst.Serial;
    public Size Device { get; private set; }
    public string LastError { get; private set; } = "";

    public bool IsReady => _connected && Device.Width > 0 && ResolveRenderWindow() != IntPtr.Zero;

    public string Describe() =>
        $"实例 {_inst.Index} · {Serial} · 设备 {Device.Width}×{Device.Height}";

    /// <summary>连接 ADB 并查询设备分辨率。失败时返回错误信息。</summary>
    public string Connect()
    {
        var (ok, msg) = _adb.Connect(Serial);
        if (!ok)
        {
            _connected = false;
            LastError = msg;
            return msg;
        }
        _connected = true;
        Device = _adb.GetDeviceSize(Serial);
        if (Device.IsEmpty)
        {
            LastError = "已连接但查询分辨率失败";
            return LastError;
        }
        LastError = "";
        return "";
    }

    // ---------------- 窗口跟踪（自适应边框） ----------------

    /// <summary>渲染子窗口有效则用之（纯渲染区，天然不含标题栏/工具栏边框），否则退回主窗口。</summary>
    private IntPtr ResolveRenderWindow()
    {
        if (Win32.IsWindow(_inst.RenderWnd) && Win32.IsWindowVisible(_inst.RenderWnd))
            return _inst.RenderWnd;
        return IntPtr.Zero;
    }

    private IntPtr ResolveRenderWindowOrMain()
    {
        var render = ResolveRenderWindow();
        if (render != IntPtr.Zero) return render;
        if (Win32.IsWindow(_inst.MainWnd)) return _inst.MainWnd;
        return IntPtr.Zero;
    }

    /// <summary>窗口失效（模拟器重启等）时按实例索引重新查询句柄与端口。</summary>
    public void RefreshWindowHandle()
    {
        try
        {
            var found = _requery().FirstOrDefault(i => i.Index == _inst.Index);
            if (found != null) _inst = found;
        }
        catch { }
    }

    /// <summary>取渲染区在屏幕上的矩形（含位置与尺寸）。</summary>
    public bool GetRenderRect(out Rectangle rect)
    {
        rect = Rectangle.Empty;
        var hwnd = ResolveRenderWindowOrMain();
        if (hwnd == IntPtr.Zero) return false;
        if (!Win32.GetClientRect(hwnd, out var rc)) return false;
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return false;
        var pt = new Win32.POINT { X = 0, Y = 0 };
        if (!Win32.ClientToScreen(hwnd, ref pt)) return false;
        rect = new Rectangle(pt.X, pt.Y, w, h);
        return true;
    }

    // ---------------- 坐标映射 ----------------

    public Point ScreenToDevice(int sx, int sy)
    {
        if (!GetRenderRect(out var r) || Device.IsEmpty) return Point.Empty;
        int x = (int)Math.Round((sx - r.X) * Device.Width / (double)r.Width);
        int y = (int)Math.Round((sy - r.Y) * Device.Height / (double)r.Height);
        return new Point(Math.Clamp(x, 0, Device.Width - 1), Math.Clamp(y, 0, Device.Height - 1));
    }

    public Point DeviceToScreen(int dx, int dy)
    {
        if (!GetRenderRect(out var r) || Device.IsEmpty) return Point.Empty;
        int x = r.X + (int)Math.Round(dx * r.Width / (double)Device.Width);
        int y = r.Y + (int)Math.Round(dy * r.Height / (double)Device.Height);
        return new Point(x, y);
    }

    /// <summary>把宏事件的坐标解析为设备坐标（device 坐标直用，屏幕坐标动态换算）。</summary>
    public Point Resolve(MacroEvent e) =>
        e.CoordSpace == "device" ? new Point(e.X, e.Y) : ScreenToDevice(e.X, e.Y);

    // ---------------- 输入注入（不占用本机鼠标） ----------------

    public bool Tap(int devX, int devY) => _adb.Tap(Serial, devX, devY);

    public bool Swipe(int devX1, int devY1, int devX2, int devY2, int durationMs) =>
        _adb.Swipe(Serial, devX1, devY1, devX2, devY2, durationMs);

    public bool Key(int keyCode) => _adb.Key(Serial, keyCode);

    /// <summary>滚轮近似：在事件位置以竖直滑动模拟滚动（adb 无滚轮接口）。</summary>
    public bool Wheel(int devX, int devY, int delta)
    {
        int dist = Math.Clamp(Math.Abs(delta) * 2, 120, 600);
        int dir = delta > 0 ? 1 : -1; // 上滚（delta>0）时手指下移
        return _adb.Swipe(Serial, devX, devY, devX, devY + dir * dist, 180);
    }

    /// <summary>截取当前模拟器画面（设备分辨率）。</summary>
    public Bitmap? Capture() => _adb.Screencap(Serial);
}

/// <summary>常用按键名 → Android keycode 映射（AOSP KeyEvent）。</summary>
internal static class AndroidKeys
{
    private static readonly Dictionary<string, int> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 66, ["tab"] = 61, ["space"] = 62, ["backspace"] = 67,
        ["delete"] = 112, ["del"] = 112, ["ins"] = 124, ["insert"] = 124,
        ["esc"] = 111, ["home"] = 3, ["end"] = 123,
        ["up"] = 19, ["down"] = 20, ["left"] = 21, ["right"] = 22,
        ["pgup"] = 92, ["pgdn"] = 93,
        [","] = 75, ["."] = 76, ["-"] = 69, ["="] = 70, ["/"] = 76,
        ["f1"] = 131, ["f2"] = 132, ["f3"] = 133, ["f4"] = 134, ["f5"] = 135, ["f6"] = 136,
        ["f7"] = 137, ["f8"] = 138, ["f9"] = 139, ["f10"] = 140, ["f11"] = 141, ["f12"] = 142
    };

    public static int FromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        name = name.Trim();
        if (Map.TryGetValue(name, out var code)) return code;
        if (name.Length == 1)
        {
            var c = char.ToLowerInvariant(name[0]);
            if (c is >= 'a' and <= 'z') return 29 + (c - 'a');       // KEYCODE_A..Z
            if (c is >= '0' and <= '9') return 7 + (c - '0');        // KEYCODE_0..9
        }
        return 0;
    }
}
