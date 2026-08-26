using System.Globalization;

namespace MacroClicker;

/// <summary>虚拟键码 <-> 可读名称 的双向映射。</summary>
internal static class KeyMap
{
    public static bool IsModifier(uint vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    /// <summary>把 L/R 变体（如 VK_LCONTROL）归并为通用键（VK_CONTROL）。</summary>
    public static uint Normalize(uint vk) => vk switch
    {
        0xA0 or 0xA1 => 0x10,
        0xA2 or 0xA3 => 0x11,
        0xA4 or 0xA5 => 0x12,
        _ => vk
    };

    private static readonly Dictionary<uint, string> Names = new()
    {
        [0x08] = "backspace", [0x09] = "tab", [0x0D] = "enter", [0x10] = "shift", [0x11] = "ctrl",
        [0x12] = "alt", [0x13] = "pause", [0x14] = "capslock", [0x1B] = "esc", [0x20] = "space",
        [0x21] = "pgup", [0x22] = "pgdn", [0x23] = "end", [0x24] = "home",
        [0x25] = "left", [0x26] = "up", [0x27] = "right", [0x28] = "down",
        [0x2C] = "printscreen", [0x2D] = "insert", [0x2E] = "delete",
        [0x5B] = "lwin", [0x5C] = "rwin", [0x5D] = "apps",
        [0x6A] = "numpad*", [0x6B] = "numpad+", [0x6D] = "numpad-", [0x6E] = "numpad.", [0x6F] = "numpad/",
        [0x90] = "numlock", [0x91] = "scrolllock",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".", [0xBF] = "/",
        [0xC0] = "`", [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
    };

    private static readonly Dictionary<string, uint> Lookup = new(StringComparer.OrdinalIgnoreCase);

    static KeyMap()
    {
        for (uint i = 0x30; i <= 0x39; i++) Names[i] = ((char)('0' + i - 0x30)).ToString();
        for (uint i = 0x41; i <= 0x5A; i++) Names[i] = ((char)('a' + i - 0x41)).ToString();
        for (uint i = 0x60; i <= 0x69; i++) Names[i] = "numpad" + (i - 0x60);
        for (uint i = 0x70; i <= 0x7B; i++) Names[i] = "f" + (i - 0x6F);

        foreach (var kv in Names) Lookup[kv.Value] = kv.Key;

        Lookup["control"] = 0x11; Lookup["lcontrol"] = 0x11; Lookup["rcontrol"] = 0x11;
        Lookup["return"] = 0x0D;
        Lookup["escape"] = 0x1B;
        Lookup["lshift"] = 0x10; Lookup["rshift"] = 0x10;
        Lookup["lalt"] = 0x12; Lookup["ralt"] = 0x12; Lookup["menu"] = 0x12;
        Lookup["win"] = 0x5B; Lookup["lwindow"] = 0x5B; Lookup["rwindow"] = 0x5C;
        Lookup["spacebar"] = 0x20;
        Lookup["del"] = 0x2E; Lookup["ins"] = 0x2D;
        Lookup["pageup"] = 0x21; Lookup["pagedown"] = 0x22;
        Lookup["plus"] = 0xBB; Lookup["minus"] = 0xBD;
        Lookup["esc"] = 0x1B; // 显式兜底
    }

    public static string NameOf(uint vk) => Names.TryGetValue(vk, out var n) ? n : $"vk0x{vk:X}";

    public static bool TryParse(string? text, out uint vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (Lookup.TryGetValue(text, out vk)) return true;
        if (text.StartsWith("vk", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h))
        { vk = h; return true; }
        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) { vk = d; return true; }
        return false;
    }
}
