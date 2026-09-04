using System.Diagnostics;
using System.Drawing;
using System.Text;

namespace MacroClicker.Emulator;

/// <summary>
/// adb.exe 命令行封装：连接设备、注入点击/滑动/按键、截图、查询分辨率。
/// 所有操作通过短生命周期进程调用，线程安全；单次 input tap 约有 200-500ms 系统延迟。
/// </summary>
internal sealed class AdbClient
{
    public string AdbPath { get; }

    public AdbClient(string adbPath) => AdbPath = adbPath;

    private (int Code, string Output) Run(string args, int timeoutMs = 15000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = AdbPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            })!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(timeoutMs);
            return (p.HasExited ? p.ExitCode : -1, output.Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private byte[]? RunBytes(string args, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = AdbPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit(timeoutMs);
            return p.HasExited && p.ExitCode == 0 && ms.Length > 8 ? ms.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>连接模拟器 ADB 端口，serial 形如 "127.0.0.1:16384"。</summary>
    public (bool Ok, string Message) Connect(string serial)
    {
        var (code, output) = Run($"connect {serial}");
        var ok = code == 0 && (output.Contains("connected", StringComparison.OrdinalIgnoreCase) ||
                               output.Contains("already", StringComparison.OrdinalIgnoreCase));
        return (ok, output.Length == 0 ? (ok ? "已连接" : "连接失败") : output);
    }

    private bool Shell(string serial, string command, int timeoutMs = 20000) =>
        Run($"-s {serial} shell {command}", timeoutMs).Code == 0;

    public bool Tap(string serial, int x, int y) => Shell(serial, $"input tap {x} {y}");

    public bool Swipe(string serial, int x1, int y1, int x2, int y2, int durationMs) =>
        Shell(serial, $"input swipe {x1} {y1} {x2} {y2} {durationMs}");

    public bool Key(string serial, int keyCode) => Shell(serial, $"input keyevent {keyCode}");

    /// <summary>查询设备逻辑分辨率（wm size），失败返回 Empty。</summary>
    public Size GetDeviceSize(string serial)
    {
        var (_, output) = Run($"-s {serial} shell wm size");
        // 输出形如 "Physical size: 1080x1920"，可能附带 "Override size: ..."（优先取 Override）
        string? physical = null, over = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Override", StringComparison.OrdinalIgnoreCase)) over = line;
            else if (line.StartsWith("Physical", StringComparison.OrdinalIgnoreCase)) physical = line;
        }
        foreach (var line in new[] { over, physical })
        {
            if (line == null) continue;
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var part = line[(idx + 1)..].Trim();
            var x = part.IndexOf('x');
            if (x <= 0) continue;
            if (int.TryParse(part[..x], out var w) && int.TryParse(part[(x + 1)..], out var h) && w > 0 && h > 0)
                return new Size(w, h);
        }
        return Size.Empty;
    }

    /// <summary>截取设备屏幕（screencap -p 输出 PNG），失败返回 null。</summary>
    public Bitmap? Screencap(string serial)
    {
        var png = RunBytes($"-s {serial} exec-out screencap -p", 30000);
        if (png == null) return null;
        try
        {
            using var ms = new MemoryStream(png);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }
}
