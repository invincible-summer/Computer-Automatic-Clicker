namespace MacroClicker.Emulator;

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
        ["`"] = 68, ["["] = 71, ["]"] = 72, [";"] = 74, ["'"] = 75,
        ["\\"] = 73, ["f1"] = 131, ["f2"] = 132, ["f3"] = 133, ["f4"] = 134, ["f5"] = 135, ["f6"] = 136,
        ["f7"] = 137, ["f8"] = 138, ["f9"] = 139, ["f10"] = 140, ["f11"] = 141, ["f12"] = 142,
        ["back"] = 4, ["menu"] = 82, ["volume_up"] = 24, ["volume_down"] = 25,
        ["power"] = 26, ["notification"] = 83, ["app_switch"] = 187
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
