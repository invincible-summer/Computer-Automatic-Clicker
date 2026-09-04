using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacroClicker;

/// <summary>宏的 JSON 保存/加载，以及界面设置的持久化。</summary>
internal static class MacroStore
{
    public sealed class MacroFile
    {
        public string Name { get; set; } = "macro";
        public int Version { get; set; } = 1;
        public List<EventDto> Events { get; set; } = new();
    }

    public sealed class EventDto
    {
        public string Type { get; set; } = "";
        public double Delay { get; set; }
        public string? Button { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public int? Delta { get; set; }
        public string? Key { get; set; }
        public List<string>? Keys { get; set; }
        public List<string>? Modifiers { get; set; }
    }

    public sealed class AppSettings
    {
        public int LoopMode { get; set; } = 0;
        public int LoopCount { get; set; } = 10;
        public double LoopInterval { get; set; } = 0;
        public string Speed { get; set; } = "1x";
        public int Countdown { get; set; } = 0;
        public bool FailSafe { get; set; } = true;
        public bool RecKeys { get; set; } = true;
        public bool RecClicks { get; set; } = true;
        public bool RecWheel { get; set; } = true;
        public bool RecDrags { get; set; } = true;
        public bool RecMoves { get; set; } = false;
        public string? LastName { get; set; }
        public string Theme { get; set; } = "dark";
    }

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string MacrosDir => Path.Combine(AppContext.BaseDirectory, "macros");

    /// <summary>启动早期读取主题偏好（深色为默认）。</summary>
    public static bool ReadThemeDark()
    {
        try
        {
            var p = Path.Combine(MacrosDir, "config.json");
            if (!File.Exists(p)) return true;
            var dto = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p), JsonOpts);
            return dto?.Theme != "light";
        }
        catch { return true; }
    }

    public static void Save(string path, string name, IList<MacroEvent> events)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var file = new MacroFile { Name = name, Events = events.Select(ToDto).ToList() };
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOpts), new UTF8Encoding(false));
    }

    public static (string Name, List<MacroEvent> Events) Load(string path)
    {
        var file = JsonSerializer.Deserialize<MacroFile>(File.ReadAllText(path), JsonOpts)
                   ?? throw new InvalidDataException("无法解析宏文件");
        var list = new List<MacroEvent>();
        foreach (var dto in file.Events) list.Add(FromDto(dto));
        return (file.Name, list);
    }

    private static bool HasPos(EventType t) =>
        t is EventType.MouseClick or EventType.MouseDown or EventType.MouseUp or EventType.MouseMove or EventType.Wheel;

    private static EventDto ToDto(MacroEvent e) => new()
    {
        Type = e.Type switch
        {
            EventType.MouseClick => "mouse_click",
            EventType.MouseDown => "mouse_down",
            EventType.MouseUp => "mouse_up",
            EventType.MouseMove => "move",
            EventType.Wheel => "wheel",
            EventType.Hotkey => "hotkey",
            EventType.Wait => "wait",
            _ => "key"
        },
        Delay = Math.Round(e.Delay, 3),
        Button = e.Type is EventType.MouseClick or EventType.MouseDown or EventType.MouseUp ? e.Button : null,
        X = HasPos(e.Type) ? e.X : null,
        Y = HasPos(e.Type) ? e.Y : null,
        Delta = e.Type == EventType.Wheel ? e.Delta : null,
        Key = e.Type == EventType.Key ? KeyMap.NameOf(e.Vk) : null,
        Keys = e.Type == EventType.Hotkey ? e.Combo.Select(KeyMap.NameOf).ToList() : null,
        Modifiers = e.Modifiers.Count > 0 ? e.Modifiers.Select(KeyMap.NameOf).ToList() : null
    };

    private static MacroEvent FromDto(EventDto d)
    {
        var t = d.Type switch
        {
            "mouse_click" => EventType.MouseClick,
            "mouse_down" => EventType.MouseDown,
            "mouse_up" => EventType.MouseUp,
            "move" => EventType.MouseMove,
            "wheel" => EventType.Wheel,
            "hotkey" => EventType.Hotkey,
            "wait" => EventType.Wait,
            _ => EventType.Key
        };
        var e = new MacroEvent
        {
            Type = t,
            Delay = Math.Max(0, d.Delay),
            Button = d.Button ?? "left",
            X = d.X ?? 0,
            Y = d.Y ?? 0,
            Delta = d.Delta ?? 120
        };
        if (!string.IsNullOrEmpty(d.Key) && KeyMap.TryParse(d.Key, out var vk)) e.Vk = vk;
        if (d.Keys != null)
            foreach (var k in d.Keys)
                if (KeyMap.TryParse(k, out var kvk)) e.Combo.Add(kvk);
        if (d.Modifiers != null)
            foreach (var m in d.Modifiers)
                if (KeyMap.TryParse(m, out var mvk)) e.Modifiers.Add(mvk);
        return e;
    }
}
