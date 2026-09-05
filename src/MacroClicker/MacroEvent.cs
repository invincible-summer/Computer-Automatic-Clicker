namespace MacroClicker;

internal enum EventType
{
    MouseClick,
    MouseDown,
    MouseUp,
    MouseMove,
    Wheel,
    Swipe,      // 模拟器/手机：滑动（含长按 = 同点滑动），device 坐标
    Key,
    Hotkey,
    Wait
}

/// <summary>宏事件所属执行目标。</summary>
internal enum MacroTarget
{
    Windows,    // 本机鼠标键盘（SendInput）
    Emulator    // 安卓模拟器/设备（ADB 注入，不占用本机鼠标）
}

/// <summary>一条宏事件。Delay 表示回放时执行该事件之前需要等待的秒数（delta time）。</summary>
internal sealed class MacroEvent
{
    public EventType Type { get; set; } = EventType.Key;
    public string Button { get; set; } = "left";          // left/right/middle/x1/x2
    public int X { get; set; }
    public int Y { get; set; }
    public int X2 { get; set; }                           // Swipe 终点
    public int Y2 { get; set; }
    public int DurationMs { get; set; } = 300;            // Swipe 时长
    public int Delta { get; set; }                        // 滚轮量
    public uint Vk { get; set; }                          // 单键
    public List<uint> Combo { get; set; } = new();        // 组合键（按下顺序）
    public List<uint> Modifiers { get; set; } = new();    // 鼠标动作附加的修饰键
    public double Delay { get; set; }
    /// <summary>null/"screen" = 屏幕像素；"device" = 模拟器设备像素（录制时换算，回放直用）。</summary>
    public string? CoordSpace { get; set; }

    public bool IsDevice => CoordSpace == "device";

    /// <summary>Swipe 且起止点相同视为长按。</summary>
    public bool IsLongPress => Type == EventType.Swipe && X == X2 && Y == Y2;

    public MacroEvent Clone() => new()
    {
        Type = Type,
        Button = Button,
        X = X,
        Y = Y,
        X2 = X2,
        Y2 = Y2,
        DurationMs = DurationMs,
        Delta = Delta,
        Vk = Vk,
        Delay = Delay,
        Combo = new List<uint>(Combo),
        Modifiers = new List<uint>(Modifiers),
        CoordSpace = CoordSpace
    };

    public string Display
    {
        get
        {
            var mods = Modifiers.Count > 0 ? string.Join("+", Modifiers.Select(KeyMap.NameOf)) + "+" : "";
            var btn = Button switch
            {
                "left" => "左键", "right" => "右键", "middle" => "中键",
                "x1" => "侧键1", "x2" => "侧键2", _ => Button
            };
            return Type switch
            {
                EventType.MouseClick => IsDevice ? $"{mods}点击(设备)" : $"{mods}鼠标{btn}点击",
                EventType.MouseDown => $"{mods}鼠标{btn}按下",
                EventType.MouseUp => $"{mods}鼠标{btn}释放",
                EventType.MouseMove => "鼠标移动",
                EventType.Wheel => $"{mods}滚轮 {(Delta > 0 ? "↑" : "↓")}",
                EventType.Swipe => IsLongPress ? $"长按 {DurationMs}ms" : "滑动",
                EventType.Key => $"按键 {KeyMap.NameOf(Vk)}",
                EventType.Hotkey => $"组合键 {string.Join("+", Combo.Select(KeyMap.NameOf))}",
                EventType.Wait => "等待",
                _ => Type.ToString()
            };
        }
    }

    public string Params => Type switch
    {
        EventType.MouseClick or EventType.MouseDown or EventType.MouseUp or EventType.MouseMove =>
            $"{X}, {Y}",
        EventType.Swipe => IsLongPress
            ? $"{X}, {Y}"
            : $"({X}, {Y}) → ({X2}, {Y2}) · {DurationMs}ms",
        EventType.Wheel => $"{Delta}",
        EventType.Key => KeyMap.NameOf(Vk),
        EventType.Hotkey => string.Join("+", Combo.Select(KeyMap.NameOf)),
        _ => ""
    };
}
