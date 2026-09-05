using System.Diagnostics;

namespace MacroClicker.Emulator;

/// <summary>发现的一个候选设备（未连接成会话）。</summary>
internal sealed record EmulatorCandidate(
    string Serial,
    string Family,
    Size DeviceSize,
    MuMuInstance? Mumu,
    Func<List<MuMuInstance>>? MumuRequery)
{
    public string Display =>
        $"{Family} · {Serial} · {DeviceSize.Width}×{DeviceSize.Height}";
}

/// <summary>
/// 模拟器/设备发现：
/// 1. 找可用的 adb.exe（MuMu 自带 → 模拟器进程目录 → PATH → Android SDK）；
/// 2. 对常见模拟器端口尝试 adb connect（MuMu 16384+32n / 雷电 5555+2n / 夜神 62001… / 逍遥 21503 / MuMu6 7555）；
/// 3. adb devices 枚举在线设备并查询分辨率；MuMu 实例附带精确渲染窗口句柄。
/// </summary>
internal static class EmulatorScanner
{
    /// <summary>发现 adb 与全部在线设备。在后台线程调用。</summary>
    public static (AdbClient? Adb, List<EmulatorCandidate> Devices, string Error) Discover()
    {
        var adbPath = FindAdb();
        if (adbPath == null)
            return (null, new List<EmulatorCandidate>(),
                "未找到 adb.exe：请安装任一模拟器（或 Android platform-tools 并加入 PATH）后重试");

        var adb = new AdbClient(adbPath);

        // 常见模拟器端口（去重后逐个尝试连接；本机未监听的端口会立即拒绝，代价很小）
        var ports = new List<(int Port, string Family)>();
        for (int i = 0; i < 5; i++) ports.Add((16384 + 32 * i, $"MuMu 12 实例 {i}"));   // MuMu 12：16384 + 32n
        ports.Add((7555, "MuMu 6"));
        for (int i = 0; i < 4; i++) ports.Add((5555 + 2 * i, i == 0 ? "雷电/蓝叠" : $"雷电 实例 {i}")); // 雷电：5555+2n
        ports.Add((62001, "夜神"));
        for (int i = 0; i < 6; i++) ports.Add((62025 + i, $"夜神 实例 {i + 1}"));
        for (int i = 0; i < 3; i++) ports.Add((21503 + i, $"逍遥 实例 {i}"));

        var seen = new HashSet<int>();
        foreach (var (port, _) in ports)
        {
            if (!seen.Add(port)) continue;
            try { adb.Connect($"127.0.0.1:{port}"); } catch { }
        }

        // MuMu 实例信息（端口 → 渲染窗口句柄）
        Dictionary<int, MuMuInstance> mumuByPort = new();
        Func<List<MuMuInstance>>? requery = null;
        string? manager = MuMuLocator.FindManagerPath();
        if (manager != null)
        {
            requery = () => MuMuLocator.QueryInstances(manager!).Instances;
            foreach (var inst in MuMuLocator.QueryInstances(manager).Instances)
                if (inst.Port > 0) mumuByPort[inst.Port] = inst;
        }

        var list = new List<EmulatorCandidate>();
        foreach (var d in adb.Devices())
        {
            if (!d.Online) continue;
            var family = GuessFamily(d.Serial, ports);
            var size = adb.GetDeviceSize(d.Serial);
            if (size.IsEmpty) continue; // 无法通信的设备跳过

            MuMuInstance? mumu = null;
            if (d.Serial.StartsWith("127.0.0.1:") && int.TryParse(d.Serial.AsSpan("127.0.0.1:".Length), out int port))
                mumuByPort.TryGetValue(port, out mumu);

            list.Add(new EmulatorCandidate(d.Serial, family, size, mumu, requery));
        }
        return (adb, list, list.Count == 0 ? "未发现在线设备：请先启动模拟器并开启其 ADB 调试" : "");
    }

    private static string GuessFamily(string serial, List<(int Port, string Family)> ports)
    {
        if (serial.StartsWith("emulator-")) return "模拟器 (AVD)";
        if (!serial.StartsWith("127.0.0.1:") && !serial.StartsWith("localhost:")) return "USB 设备";
        if (int.TryParse(serial.AsSpan(serial.IndexOf(':') + 1), out int port))
        {
            foreach (var (p, f) in ports)
                if (p == port) return f;
        }
        return "自定义设备";
    }

    /// <summary>依次尝试：MuMu 自带 adb → 运行中的模拟器进程目录 → PATH → Android SDK 默认位置。</summary>
    public static string? FindAdb()
    {
        // 1) MuMuManager 同目录 adb（避免与系统 adb 版本冲突）
        var manager = MuMuLocator.FindManagerPath();
        var near = MuMuLocator.FindAdbNearManager(manager);
        if (near != null) return near;

        // 2) 运行中的模拟器进程目录（雷电/夜神/逍遥/蓝叠等均随包携带 adb）
        foreach (var name in EmulatorProcesses.Names)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (string.IsNullOrEmpty(exe)) continue;
                    var dir = Path.GetDirectoryName(exe)!;
                    var candidates = new[]
                    {
                        Path.Combine(dir, "adb.exe"),
                        Path.Combine(dir, "noxAdb.exe"),
                        Path.Combine(dir, "HD-Adb.exe"),
                        Path.Combine(dir, "bin", "adb.exe"),
                        Path.Combine(dir, "bin", "nox_adb.exe")
                    };
                    foreach (var c in candidates)
                        if (File.Exists(c) && TestAdb(c)) return c;
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        // 3) PATH 中的 adb
        var pathAdb = WhichAdb();
        if (pathAdb != null) return pathAdb;

        // 4) Android SDK 默认位置
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "platform-tools", "adb.exe");
        return File.Exists(sdk) && TestAdb(sdk) ? sdk : null;
    }

    private static string? WhichAdb()
    {
        try
        {
            var searchDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in searchDirs)
            {
                var full = Path.GetFullPath(Path.Combine(dir.Trim('"'), "adb.exe"));
                if (File.Exists(full) && TestAdb(full)) return full;
            }
        }
        catch { }
        return null;
    }

    private static bool TestAdb(string path)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = "version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0 && output.Contains("Android Debug Bridge", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
