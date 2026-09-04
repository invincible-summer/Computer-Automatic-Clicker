using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MacroClicker.Emulator;

/// <summary>MuMu 模拟器的一个实例信息（来自 MuMuManager.exe info）。</summary>
internal sealed record MuMuInstance(
    int Index,
    string Name,
    string Host,
    int Port,
    IntPtr MainWnd,
    IntPtr RenderWnd,
    bool AndroidStarted,
    bool ProcessStarted)
{
    public string Serial => $"{Host}:{Port}";
    public bool Running => ProcessStarted && Port > 0;
}

/// <summary>
/// 定位 MuMu 模拟器：搜索 MuMuManager.exe（官方命令行接口），
/// 并通过 `info -v all` 拿到每个实例的 ADB 端口与主窗口/渲染窗口句柄。
/// </summary>
internal static class MuMuLocator
{
    /// <summary>依次尝试：用户配置 → 运行中的 MuMu 进程 → 默认安装目录 → 注册表卸载信息。</summary>
    public static string? FindManagerPath(string? hint = null)
    {
        if (!string.IsNullOrWhiteSpace(hint) && File.Exists(hint)) return hint;

        // 1) 运行中的模拟器进程：MuMuPlayer.exe 与 MuMuManager.exe 同在 shell\ 目录
        foreach (var name in new[] { "MuMuPlayer", "MuMuPlayerGlobal", "MuMuNxMain", "NemuPlayer" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (string.IsNullOrEmpty(exe)) continue;
                    var candidate = Path.Combine(Path.GetDirectoryName(exe)!, "MuMuManager.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        // 2) 常见默认安装位置（含国际版）
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddRoot(string? env) { if (!string.IsNullOrEmpty(env)) roots.Add(env); }
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddRoot(Environment.GetEnvironmentVariable("ProgramFiles(x86)"));
        roots.Add("C:\\Program Files");
        roots.Add("D:\\Program Files");
        foreach (var root in roots)
        {
            var netease = Path.Combine(root, "Netease");
            foreach (var dir in SafeDirs(netease).Concat(SafeDirs(root)))
            {
                var candidate = Path.Combine(dir, "shell", "MuMuManager.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        // 3) 注册表卸载信息（HKLM / HKLM WOW6432Node / HKCU）
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine, view);
                foreach (var sub in UninstallPaths)
                {
                    using var k = baseKey.OpenSubKey(sub);
                    if (k == null) continue;
                    foreach (var child in k.GetSubKeyNames())
                    {
                        using var c = k.OpenSubKey(child);
                        var display = c?.GetValue("DisplayName") as string ?? "";
                        if (!display.Contains("MuMu", StringComparison.OrdinalIgnoreCase)) continue;
                        var loc = (c?.GetValue("InstallLocation") ?? c?.GetValue("UninstallString")) as string;
                        var found = ResolveManagerFromLocation(loc);
                        if (found != null) return found;
                    }
                }
            }
            catch { }
        }
        {
            // HKCU 也查一遍
            try
            {
                foreach (var sub in UninstallPaths)
                {
                    using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sub);
                    if (k == null) continue;
                    foreach (var child in k.GetSubKeyNames())
                    {
                        using var c = k.OpenSubKey(child);
                        var display = c?.GetValue("DisplayName") as string ?? "";
                        if (!display.Contains("MuMu", StringComparison.OrdinalIgnoreCase)) continue;
                        var loc = (c?.GetValue("InstallLocation") ?? c?.GetValue("UninstallString")) as string;
                        var found = ResolveManagerFromLocation(loc);
                        if (found != null) return found;
                    }
                }
            }
            catch { }
        }
        return null;
    }

    private static readonly string[] UninstallPaths =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    };

    private static string? ResolveManagerFromLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        var path = location.Trim().Trim('"');
        // UninstallString 指向卸载程序时，取其所在目录
        var dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        if (string.IsNullOrEmpty(dir)) return null;
        var candidates = new[]
        {
            Path.Combine(dir, "shell", "MuMuManager.exe"),
            Path.Combine(dir, "MuMuManager.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>与 MuMuManager.exe 同目录的自带 adb.exe（推荐优先使用，避免系统 adb 版本冲突）。</summary>
    public static string? FindAdbNearManager(string? managerPath)
    {
        if (string.IsNullOrEmpty(managerPath)) return null;
        var adb = Path.Combine(Path.GetDirectoryName(managerPath)!, "adb.exe");
        return File.Exists(adb) ? adb : null;
    }

    /// <summary>执行 `MuMuManager.exe info -v all` 并解析全部实例。</summary>
    public static (List<MuMuInstance> Instances, string Error) QueryInstances(string managerPath)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = managerPath,
                Arguments = "info -v all",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            })!;
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            if (string.IsNullOrWhiteSpace(json))
                return (new List<MuMuInstance>(), "MuMuManager 未返回数据（模拟器可能未安装或版本过旧，需要 V4.0.0+）");

            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            var list = new List<MuMuInstance>();
            foreach (var item in EnumerateInstances(doc.RootElement))
                list.Add(ParseInstance(item));
            return (list, list.Count == 0 ? "未找到任何模拟器实例" : "");
        }
        catch (Exception ex)
        {
            return (new List<MuMuInstance>(), "查询实例失败：" + ex.Message);
        }
    }

    /// <summary>兼容数组、单对象、按索引键的对象三种输出形态。</summary>
    private static IEnumerable<JsonElement> EnumerateInstances(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in root.EnumerateArray()) yield return e;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("index", out _))
            {
                yield return root;
            }
            else
            {
                foreach (var prop in root.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                        yield return prop.Value;
            }
        }
    }

    private static MuMuInstance ParseInstance(JsonElement e)
    {
        int index = e.TryGetProperty("index", out var idx) ? ParseInt(idx) : -1;
        string name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "MuMu";
        string host = e.TryGetProperty("adb_host_ip", out var h) ? h.GetString() ?? "127.0.0.1" : "127.0.0.1";
        int port = e.TryGetProperty("adb_port", out var pt) ? ParseInt(pt) : 0;
        return new MuMuInstance(
            index, name, host, port,
            ParseWnd(e, "main_wnd"),
            ParseWnd(e, "render_wnd"),
            e.TryGetProperty("is_android_started", out var a) && a.ValueKind == JsonValueKind.True,
            e.TryGetProperty("is_process_started", out var ps) && ps.ValueKind == JsonValueKind.True);
    }

    private static int ParseInt(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetInt32(),
        JsonValueKind.String => int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0,
        _ => 0
    };

    /// <summary>窗口句柄以十六进制字符串返回，如 "00840F4E"。</summary>
    private static IntPtr ParseWnd(JsonElement e, string field)
    {
        if (!e.TryGetProperty(field, out var w)) return IntPtr.Zero;
        if (w.ValueKind == JsonValueKind.String)
        {
            var s = w.GetString();
            if (string.IsNullOrEmpty(s)) return IntPtr.Zero;
            try { return new IntPtr(Convert.ToInt64(s, 16)); }
            catch { return IntPtr.Zero; }
        }
        if (w.ValueKind == JsonValueKind.Number) return new IntPtr(w.GetInt64());
        return IntPtr.Zero;
    }
}
