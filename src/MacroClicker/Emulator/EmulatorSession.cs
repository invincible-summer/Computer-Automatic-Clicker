using System.Drawing;

namespace MacroClicker.Emulator;

/// <summary>常见的模拟器窗口进程名（用于通用录制窗口识别）。</summary>
internal static class EmulatorProcesses
{
    public static readonly string[] Names =
    {
        "MuMuPlayer", "MuMuPlayerGlobal", "MuMuNxMain", "NemuPlayer",       // 网易 MuMu
        "dnplayer", "Ld9MuHeadlessWindow", "LdBoxHeadless",                  // 雷电
        "Nox", "NoxVMHandle", "NoxHandle",                                   // 夜神
        "MEmu", "MEmuHeadless",                                              // 逍遥
        "HD-Player", "HD-RunApp",                                            // BlueStacks 蓝叠
        "TGB", "TBSandbox",                                                  // 腾讯手游助手
        "qemu-system-x86_64", "emulator-x86_64", "emulator64-x64"            // Google AVD
    };

    public static bool IsEmulatorProcess(string processName) =>
        Names.Contains(processName, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 模拟器/设备会话：保存 serial、设备分辨率与（MuMu 的）实例窗口信息，
/// 负责录制时的「屏幕坐标 → 设备坐标」换算与回放时的 ADB 注入——全程不占用本机鼠标。
/// </summary>
internal sealed class EmulatorSession
{
    private readonly AdbClient _adb;
    private readonly Func<List<MuMuInstance>>? _mumuRequery;
    private MuMuInstance? _mumu;

    public EmulatorSession(AdbClient adb, string serial, string family, MuMuInstance? mumu = null, Func<List<MuMuInstance>>? mumuRequery = null)
    {
        _adb = adb;
        Serial = serial;
        Family = family;
        _mumu = mumu;
        _mumuRequery = mumuRequery;
    }

    public string Serial { get; }
    public string Family { get; }
    public Size Device { get; private set; }
    public string LastError { get; private set; } = "";

    public bool IsReady => Device.Width > 0 && _adb.IsOnline(Serial);

    public string Describe() => $"{Family} · {Serial} · 设备 {Device.Width}×{Device.Height}";

    /// <summary>连接并查询设备分辨率。失败返回错误信息。</summary>
    public string Connect()
    {
        if (Serial.Contains(':'))
        {
            var (ok, msg) = _adb.Connect(Serial);
            if (!ok)
            {
                LastError = msg;
                return msg;
            }
        }
        Device = _adb.GetDeviceSize(Serial);
        if (Device.IsEmpty)
        {
            LastError = "已连接但查询分辨率失败";
            return LastError;
        }
        LastError = "";
        return "";
    }

    /// <summary>设备掉线时尝试重连（一次）。成功返回 true。</summary>
    public bool TryRecover()
    {
        if (_adb.IsOnline(Serial)) return true;
        if (Serial.Contains(':')) _adb.Connect(Serial);
        if (!_adb.IsOnline(Serial)) return false;
        if (Device.IsEmpty) Device = _adb.GetDeviceSize(Serial);
        return Device.Width > 0;
    }

    // ---------------- 录制时的坐标映射（自适应窗口移动/缩放） ----------------

    /// <summary>MuMu 实例的渲染窗口（纯渲染区，不含标题栏/边框），无效时为 Zero。</summary>
    private IntPtr RenderWindow()
    {
        if (_mumu == null) return IntPtr.Zero;
        if (Win32.IsWindow(_mumu.RenderWnd) && Win32.IsWindowVisible(_mumu.RenderWnd))
            return _mumu.RenderWnd;
        if (Win32.IsWindow(_mumu.MainWnd) && Win32.IsWindowVisible(_mumu.MainWnd))
            return _mumu.MainWnd;
        return IntPtr.Zero;
    }

    private static Rectangle ClientRectOf(IntPtr hwnd)
    {
        if (!Win32.GetClientRect(hwnd, out var rc)) return Rectangle.Empty;
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return Rectangle.Empty;
        var pt = new Win32.POINT { X = 0, Y = 0 };
        if (!Win32.ClientToScreen(hwnd, ref pt)) return Rectangle.Empty;
        return new Rectangle(pt.X, pt.Y, w, h);
    }

    /// <summary>
    /// 屏幕 → 设备坐标。优先用 MuMu 实例渲染窗口精确映射；
    /// 否则取光标处窗口的根窗口，若属于已知模拟器进程则用其客户区映射（适配各类模拟器）。
    /// </summary>
    public bool TryMapScreen(int sx, int sy, out Point device)
    {
        device = Point.Empty;
        if (Device.IsEmpty) return false;

        Rectangle rect;
        var render = RenderWindow();
        if (render != IntPtr.Zero)
        {
            rect = ClientRectOf(render);
            if (rect.Width <= 0 || !rect.Contains(sx, sy))
            {
                // 点不在 MuMu 窗口内：也不允许误映射到其他窗口（多开场景）
                return false;
            }
        }
        else
        {
            // 通用路径：光标处的根窗口须是已知模拟器进程
            var hwnd = Win32.WindowFromPoint(new Win32.POINT { X = sx, Y = sy });
            if (hwnd == IntPtr.Zero) return false;
            var root = Win32.GetAncestor(hwnd, 2);
            if (root == IntPtr.Zero) root = hwnd;
            if (!IsEmulatorWindow(root)) return false;
            rect = ClientRectOf(root);
            if (rect.Width <= 0 || !rect.Contains(sx, sy)) return false;
        }

        int x = (int)Math.Round((sx - rect.X) * Device.Width / (double)rect.Width);
        int y = (int)Math.Round((sy - rect.Y) * Device.Height / (double)rect.Height);
        device = new Point(Math.Clamp(x, 0, Device.Width - 1), Math.Clamp(y, 0, Device.Height - 1));
        return true;
    }

    private static bool IsEmulatorWindow(IntPtr root)
    {
        try
        {
            _ = Win32.GetWindowThreadProcessId(root, out uint pid);
            if (pid == 0) return false;
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return EmulatorProcesses.IsEmulatorProcess(p.ProcessName);
        }
        catch { return false; }
    }

    /// <summary>当前前台窗口是否属于模拟器（模拟器模式下用于过滤键盘录制）。</summary>
    public bool EmulatorIsForeground()
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var render = RenderWindow();
        if (render != IntPtr.Zero)
        {
            if (fg == render) return true;
            var root = Win32.GetAncestor(render, 2);
            if (root != IntPtr.Zero && fg == root) return true;
        }
        return IsEmulatorWindow(fg);
    }

    /// <summary>MuMu 实例窗口失效（模拟器重启）时按索引重新查询。</summary>
    public void RefreshInstance()
    {
        try
        {
            if (_mumuRequery == null) return;
            var found = _mumuRequery().FirstOrDefault(i => i.Index == _mumu?.Index);
            if (found != null) _mumu = found;
        }
        catch { }
    }

    // ---------------- 输入注入（不占用本机鼠标） ----------------

    public bool Tap(int devX, int devY) => _adb.Tap(Serial, devX, devY);

    public bool Swipe(int devX1, int devY1, int devX2, int devY2, int durationMs) =>
        _adb.Swipe(Serial, devX1, devY1, devX2, devY2, durationMs);

    public bool Key(int keyCode) => _adb.Key(Serial, keyCode);

    /// <summary>旧版滚轮事件（device 坐标）近似为竖直滑动。</summary>
    public bool WheelSwipe(int devX, int devY, int delta)
    {
        int dist = Math.Clamp(Math.Abs(delta) * 2, 120, 600);
        int dir = delta > 0 ? 1 : -1; // 上滚（delta>0）时手指下移
        int y2 = Math.Clamp(devY + dir * dist, 0, Device.Height - 1);
        return Swipe(devX, devY, devX, y2, 180);
    }

    /// <summary>把宏事件的坐标解析为设备坐标（device 直用；屏幕坐标尝试动态换算）。</summary>
    public Point Resolve(MacroEvent e)
    {
        if (e.CoordSpace == "device") return new Point(e.X, e.Y);
        return TryMapScreen(e.X, e.Y, out var p) ? p : new Point(-1, -1);
    }
}
