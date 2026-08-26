using System.Runtime.InteropServices;

namespace MacroClicker;

/// <summary>通过 SendInput 模拟鼠标/键盘输入（在播放线程上调用）。</summary>
internal static class Simulator
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint KeyEventfExtendedKey = 0x0001;
    private const uint KeyEventfKeyUp = 0x0002;

    private const uint MouseEventfLeftDown = 0x0002;
    private const uint MouseEventfLeftUp = 0x0004;
    private const uint MouseEventfRightDown = 0x0008;
    private const uint MouseEventfRightUp = 0x0010;
    private const uint MouseEventfMiddleDown = 0x0040;
    private const uint MouseEventfMiddleUp = 0x0080;
    private const uint MouseEventfXDown = 0x0080;
    private const uint MouseEventfXUp = 0x0100;
    private const uint MouseEventfWheel = 0x0800;

    public static void Button(string button, bool down)
    {
        uint flags;
        var data = 0u;
        switch (button)
        {
            case "right":
                flags = down ? MouseEventfRightDown : MouseEventfRightUp;
                break;
            case "middle":
                flags = down ? MouseEventfMiddleDown : MouseEventfMiddleUp;
                break;
            case "x1":
            case "x2":
                flags = down ? MouseEventfXDown : MouseEventfXUp;
                data = (uint)((button == "x1" ? 1 : 2) << 16);
                break;
            default:
                flags = down ? MouseEventfLeftDown : MouseEventfLeftUp;
                break;
        }
        SendMouse(flags, data);
    }

    public static void Wheel(int delta) =>
        SendMouse(MouseEventfWheel, unchecked((uint)(delta << 16)));

    public static void Key(uint vk, bool down)
    {
        uint flags = 0;
        if (IsExtended(vk)) flags |= KeyEventfExtendedKey;
        if (!down) flags |= KeyEventfKeyUp;
        Send(new Win32.INPUT
        {
            type = InputKeyboard,
            u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = unchecked((ushort)vk), dwFlags = flags } }
        });
    }

    // 这些键的扫描码带扩展前缀 E0，不标记会导致部分程序识别错键
    private static bool IsExtended(uint vk) =>
        vk is (>= 0x21 and <= 0x28) or 0x2D or 0x2E or 0x5C or 0x5D or 0x6F or 0xA3 or 0xA5;

    private static void SendMouse(uint flags, uint data) =>
        Send(new Win32.INPUT
        {
            type = InputMouse,
            u = new Win32.InputUnion { mi = new Win32.MOUSEINPUT { dwFlags = flags, mouseData = data } }
        });

    private static void Send(params Win32.INPUT[] inputs) =>
        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
}
