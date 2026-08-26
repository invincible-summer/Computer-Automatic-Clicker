using System.Runtime.InteropServices;

namespace MacroClicker;

/// <summary>
/// 全局键盘/鼠标低级钩子（只在录制期间安装）。
/// 必须在带消息循环的 UI 线程上 Install/Uninstall。
/// 自动忽略程序注入的输入（LLKHF_INJECTED），避免回放被误录。
/// </summary>
internal sealed class GlobalHook : IDisposable
{
    private readonly Win32.HookProc _kbdProc;
    private readonly Win32.HookProc _mouseProc;
    private IntPtr _kbdHook;
    private IntPtr _mouseHook;
    private bool _installed;

    /// <summary>按键事件：vk（已归并 L/R 变体）、是否按下。</summary>
    public event Action<uint, bool>? KeyEvent;

    /// <summary>鼠标事件：消息、x、y、X 键编号（1/2，其余为 0）。</summary>
    public event Action<int, int, int, int>? MouseEvent;

    /// <summary>滚轮事件：x、y、delta（±120 一格）。</summary>
    public event Action<int, int, int>? WheelEvent;

    public GlobalHook()
    {
        _kbdProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    public void Install()
    {
        if (_installed) return;
        var mod = Win32.GetModuleHandle(null);
        _kbdHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _kbdProc, mod, 0);
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, mod, 0);
        _installed = true;
        if (_kbdHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            Uninstall();
            throw new InvalidOperationException($"安装全局输入钩子失败（Win32 错误码 {err}）");
        }
    }

    public void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        if (_kbdHook != IntPtr.Zero) { Win32.UnhookWindowsHookEx(_kbdHook); _kbdHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { Win32.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }

    public void Dispose() => Uninstall();

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            if ((k.flags & Win32.LLKHF_INJECTED) == 0)
            {
                int msg = wParam.ToInt32();
                if (msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN)
                    KeyEvent?.Invoke(KeyMap.Normalize(k.vkCode), true);
                else if (msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP)
                    KeyEvent?.Invoke(KeyMap.Normalize(k.vkCode), false);
            }
        }
        return Win32.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var m = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            if ((m.flags & Win32.LLKHF_INJECTED) == 0)
            {
                int msg = wParam.ToInt32();
                if (msg == Win32.WM_MOUSEWHEEL)
                {
                    short delta = unchecked((short)((m.mouseData >> 16) & 0xFFFF));
                    WheelEvent?.Invoke(m.pt.X, m.pt.Y, delta);
                }
                else if (msg is Win32.WM_XBUTTONDOWN or Win32.WM_XBUTTONUP)
                {
                    int xb = unchecked((int)((m.mouseData >> 16) & 0xFFFF));
                    MouseEvent?.Invoke(msg, m.pt.X, m.pt.Y, xb);
                }
                else
                {
                    MouseEvent?.Invoke(msg, m.pt.X, m.pt.Y, 0);
                }
            }
        }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }
}
