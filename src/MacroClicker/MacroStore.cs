using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacroClicker;

/// <summary>宏的 JSON 保存/加载、宏库目录管理，以及界面设置的持久化。</summary>
internal static class MacroStore
{
    public sealed class MacroFile
    {
        public string Name { get; set; } = "macro";
        public int Version { get; set; } = 2;
        /// <summary>"windows" / "emulator"；旧版宏缺省视为 windows。</summary>
        public string? Target { get; set; }
        public List<EventDto> Events { get; set; } = new();
    }

    public sealed class EventDto
    {
        public string Type { get; set; } = "";
        public double Delay { get; set; }
        public string? Button { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public int? X2 { get; set; }
        public int? Y2 { get; set; }
        public int? Duration { get; set; }
        public int? Delta { get; set; }
        public string? Key { get; set; }
        public List<string>? Keys { get; set; }
        public List<string>? Modifiers { get; set; }
        public string? CoordSpace { get; set; }
    }

    /// <summary>单个目标的执行/录制选项（windows 与 emulator 各一份）。</summary>
    public sealed class TargetSettings
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
    }

    public sealed class AppSettings
    {
        public string Theme { get; set; } = "dark";
        public TargetSettings Win { get; set; } = new();
        public TargetSettings Emu { get; set; } = new();
        /// <summary>上次选择的模拟器设备 serial（用于自动选中下拉项）。</summary>
        public string? EmuSerial { get; set; }
        public int WinW { get; set; } = 0;
        public int WinH { get; set; } = 0;
        public bool WinMax { get; set; } = false;
    }

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string MacrosDir => Path.Combine(AppContext.BaseDirectory, "macros");

    /// <summary>按目标分目录：macros/windows、macros/emulator。</summary>
    public static string TargetDir(MacroTarget target) =>
        Path.Combine(MacrosDir, target == MacroTarget.Emulator ? "emulator" : "windows");

    /// <summary>旧版（单目录平铺）宏迁移到 windows/ 子目录；仅执行一次幂等迁移。</summary>
    public static void EnsureMigrated()
    {
        try
        {
            Directory.CreateDirectory(TargetDir(MacroTarget.Windows));
            Directory.CreateDirectory(TargetDir(MacroTarget.Emulator));
            var marker = Path.Combine(MacrosDir, ".migrated");
            if (File.Exists(marker)) return;
            foreach (var f in new DirectoryInfo(MacrosDir).GetFiles("*.json"))
            {
                if (f.Name.Equals("config.json", StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(TargetDir(MacroTarget.Windows), f.Name);
                if (!File.Exists(dest)) f.MoveTo(dest);
            }
            File.WriteAllText(marker, "1");
        }
        catch { }
    }

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

    // ---------------- 宏库（macros 目录 + 下拉选择，不再使用文件对话框） ----------------

    /// <summary>列出某目标的全部宏（按修改时间倒序）。</summary>
    public static List<(string Name, string Path)> ListMacros(MacroTarget target)
    {
        var dir = new DirectoryInfo(TargetDir(target));
        if (!dir.Exists) return new();
        return dir.GetFiles("*.json")
            .Select(f => (Name: Path.GetFileNameWithoutExtension(f.Name), Path: f.FullName))
            .OrderByDescending(t => File.GetLastWriteTimeUtc(t.Path))
            .ToList();
    }

    /// <summary>把宏名规范为安全文件名。</summary>
    public static string SanitizeName(string name)
    {
        var chars = name.Trim().ToCharArray()
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
            .ToArray();
        var s = new string(chars).Trim();
        return s.Length == 0 ? "macro" : s;
    }

    public static void Save(string path, string name, MacroTarget target, IList<MacroEvent> events)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var file = new MacroFile
        {
            Name = name,
            Target = target == MacroTarget.Emulator ? "emulator" : "windows",
            Events = events.Select(ToDto).ToList()
        };
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

    public static void DeleteMacro(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>重命名宏：重写为新文件（同步 JSON 内 Name）后删除旧文件。</summary>
    public static void Rename(MacroTarget target, string oldName, string newName)
    {
        var oldPath = PathOf(target, oldName);
        var newPath = PathOf(target, newName);
        if (!File.Exists(oldPath)) throw new FileNotFoundException("宏文件不存在", oldPath);
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(newPath)) throw new IOException($"宏「{newName}」已存在");
        var (_, events) = Load(oldPath);
        Save(newPath, newName, target, events);
        File.Delete(oldPath);
    }

    public static string PathOf(MacroTarget target, string name) =>
        Path.Combine(TargetDir(target), SanitizeName(name) + ".json");

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
            EventType.Swipe => "swipe",
            EventType.Hotkey => "hotkey",
            EventType.Wait => "wait",
            _ => "key"
        },
        Delay = Math.Round(e.Delay, 3),
        Button = e.Type is EventType.MouseClick or EventType.MouseDown or EventType.MouseUp ? e.Button : null,
        X = HasPos(e.Type) || e.Type == EventType.Swipe ? e.X : null,
        Y = HasPos(e.Type) || e.Type == EventType.Swipe ? e.Y : null,
        X2 = e.Type == EventType.Swipe ? e.X2 : null,
        Y2 = e.Type == EventType.Swipe ? e.Y2 : null,
        Duration = e.Type == EventType.Swipe ? e.DurationMs : null,
        Delta = e.Type == EventType.Wheel ? e.Delta : null,
        Key = e.Type == EventType.Key ? KeyMap.NameOf(e.Vk) : null,
        Keys = e.Type == EventType.Hotkey ? e.Combo.Select(KeyMap.NameOf).ToList() : null,
        Modifiers = e.Modifiers.Count > 0 ? e.Modifiers.Select(KeyMap.NameOf).ToList() : null,
        CoordSpace = e.CoordSpace == "device" ? "device" : null
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
            "swipe" => EventType.Swipe,
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
            X2 = d.X2 ?? d.X ?? 0,
            Y2 = d.Y2 ?? d.Y ?? 0,
            DurationMs = Math.Clamp(d.Duration ?? 300, 50, 60000),
            Delta = d.Delta ?? 120,
            CoordSpace = d.CoordSpace == "device" ? "device" : null
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
