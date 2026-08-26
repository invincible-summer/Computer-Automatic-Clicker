namespace MacroClicker;

internal enum EventType
{
    MouseClick,
    MouseDown,
    MouseUp,
    MouseMove,
    Wheel,
    Key,
    Hotkey,
    Wait
}

/// <summary>一条宏事件。Delay 表示回放时执行该事件之前需要等待的秒数（delta time）。</summary>
internal sealed class MacroEvent
{
    public EventType Type { get; set; } = EventType.Key;
    public string Button { get; set; } = "left";          // left/right/middle/x1/x2
    public int X { get; set; }
    public int Y { get; set; }
    public int Delta { get; set; }                        // 滚轮量
    public uint Vk { get; set; }                          // 单键
    public List<uint> Combo { get; set; } = new();        // 组合键（按下顺序）
    public List<uint> Modifiers { get; set; } = new();    // 鼠标动作附加的修饰键
    public double Delay { get; set; }

    public MacroEvent Clone() => new()
    {
        Type = Type,
        Button = Button,
        X = X,
        Y = Y,
        Delta = Delta,
        Vk = Vk,
        Delay = Delay,
        Combo = new List<uint>(Combo),
        Modifiers = new List<uint>(Modifiers)
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
                EventType.MouseClick => $"{mods}鼠标{btn}点击",
                EventType.MouseDown => $"{mods}鼠标{btn}按下",
                EventType.MouseUp => $"{mods}鼠标{btn}释放",
                EventType.MouseMove => "鼠标移动",
                EventType.Wheel => $"{mods}滚轮 {(Delta > 0 ? "↑" : "↓")}",
                EventType.Key => $"按键 {KeyMap.NameOf(Vk)}",
                EventType.Hotkey => $"组合键 {string.Join("+", Combo.Select(KeyMap.NameOf))}",
                EventType.Wait => "等待",
                _ => Type.ToString()
            };
        }
    }

    public string Params => Type switch
    {
        EventType.MouseClick or EventType.MouseDown or EventType.MouseUp or EventType.MouseMove => $"{X}, {Y}",
        EventType.Wheel => $"{Delta}",
        EventType.Key => KeyMap.NameOf(Vk),
        EventType.Hotkey => string.Join("+", Combo.Select(KeyMap.NameOf)),
        _ => ""
    };
}
