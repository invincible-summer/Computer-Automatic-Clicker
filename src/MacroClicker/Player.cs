using System.Diagnostics;
using MacroClicker.Emulator;

namespace MacroClicker;

internal enum LoopMode { Once = 0, Count = 1, Infinite = 2 }

internal sealed class PlaySettings
{
    public LoopMode Mode = LoopMode.Once;
    public int Count = 1;
    public double LoopInterval;     // 每轮之间的额外间隔（秒）
    public double Speed = 1.0;      // 播放速度倍率，delay/speed
    public int CountdownSeconds;    // 播放前倒计时
    public bool FailSafe = true;    // 鼠标移到屏幕左上角紧急停止
    /// <summary>模拟器模式：鼠标/按键事件经 ADB 注入模拟器，不占用本机鼠标。</summary>
    public bool UseEmulator;
}

/// <summary>回放引擎：后台线程按 delta time 回放事件序列，支持循环、倍速、暂停与急停。</summary>
internal sealed class Player
{
    private volatile bool _stop;
    private bool _failSafeReported;
    private readonly ManualResetEventSlim _run = new(true);
    private Thread? _thread;
    private DateTime _lastStatusUtc = DateTime.MinValue;
    private EmulatorSession? _emu;
    private readonly HashSet<string> _warned = new();

    public bool IsBusy { get; private set; }
    public bool IsPaused { get; private set; }

    /// <summary>非急停的停止原因（如模拟器断开），由 UI 读取后展示并清空。</summary>
    public string? StopReason { get; set; }

    /// <summary>状态文本（后台线程回调）。</summary>
    public event Action<string>? Status;

    /// <summary>触发了左上角急停（后台线程回调）。</summary>
    public event Action? AbortedByFailSafe;

    /// <summary>回放结束：true = 正常完成，false = 被停止/急停。</summary>
    public event Action<bool>? Finished;

    public void Start(List<MacroEvent> events, PlaySettings settings, EmulatorSession? emu = null)
    {
        if (IsBusy) return;
        _stop = false;
        _failSafeReported = false;
        StopReason = null;
        IsPaused = false;
        _run.Set();
        _lastStatusUtc = DateTime.MinValue;
        _emu = settings.UseEmulator && emu != null && emu.IsReady ? emu : null;
        _warned.Clear();
        if (settings.UseEmulator && _emu == null)
            Status?.Invoke("⚠ 模拟器未连接，回退为本机鼠标执行");
        _thread = new Thread(() => Run(events, settings)) { IsBackground = true, Name = "MacroPlayer" };
        _thread.Start();
    }

    public void Stop() { _stop = true; _run.Set(); }

    public void Pause() { if (IsBusy) { IsPaused = true; _run.Reset(); } }

    public void Resume() { if (IsBusy) { IsPaused = false; _run.Set(); } }

    private void Run(List<MacroEvent> events, PlaySettings s)
    {
        IsBusy = true;
        try
        {
            for (int c = s.CountdownSeconds; c > 0; c--)
            {
                Status?.Invoke($"{c} 秒后开始执行…");
                if (!SleepInterruptible(1.0, s)) { Finish(false); return; }
            }

            int loop = 0;
            while (true)
            {
                loop++;
                for (int i = 0; i < events.Count; i++)
                {
                    if (_stop) { Finish(false); return; }
                    if (s.FailSafe && FailSafeCorner()) { AbortFailSafe(); Finish(false); return; }

                    var d = events[i].Delay / s.Speed;
                    if (d > 0 && !SleepInterruptible(d, s)) { Finish(false); return; }

                    StatusThrottled($"▶ 执行中 · 第 {loop} 轮 · 事件 {i + 1}/{events.Count}");
                    if (_emu != null) ExecuteEmu(events[i]);
                    else Execute(events[i]);
                }

                if (s.Mode == LoopMode.Once) break;
                if (s.Mode == LoopMode.Count && loop >= s.Count) break;
                if (s.LoopInterval > 0 && !SleepInterruptible(s.LoopInterval, s)) { Finish(false); return; }
            }
            Finish(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Finish(bool completed) => Finished?.Invoke(completed);

    private void StatusThrottled(string text)
    {
        if ((DateTime.UtcNow - _lastStatusUtc).TotalMilliseconds < 100) return;
        _lastStatusUtc = DateTime.UtcNow;
        Status?.Invoke(text);
    }

    private void AbortFailSafe()
    {
        _stop = true;
        _run.Set();
        if (!_failSafeReported)
        {
            _failSafeReported = true;
            AbortedByFailSafe?.Invoke();
        }
    }

    private static bool FailSafeCorner()
    {
        if (!Win32.GetCursorPos(out var p)) return false;
        return p.X <= 4 && p.Y <= 4;
    }

    /// <summary>可中断的等待：支持暂停（暂停期间不计时）、停止、左上角急停。</summary>
    private bool SleepInterruptible(double seconds, PlaySettings s)
    {
        double waited = 0;
        while (waited < seconds)
        {
            if (!_run.IsSet)
            {
                while (!_run.Wait(200))
                {
                    if (_stop) return false;
                    if (s.FailSafe && FailSafeCorner()) { AbortFailSafe(); return false; }
                }
            }
            if (_stop) return false;
            if (s.FailSafe && FailSafeCorner()) { AbortFailSafe(); return false; }

            var slice = Stopwatch.StartNew();
            Thread.Sleep(10);
            waited += slice.Elapsed.TotalSeconds;
        }
        return true;
    }

    private void WarnOnce(string key, string msg)
    {
        if (_warned.Add(key)) Status?.Invoke("⚠ " + msg);
    }

    /// <summary>模拟器执行路径：坐标动态换算为设备像素后经 ADB 注入（不移动本机鼠标）。</summary>
    private void ExecuteEmu(MacroEvent e)
    {
        var emu = _emu!;
        // 窗口关闭/模拟器重启会导致坐标无法换算——此时绝不能盲点 (0,0)，先刷新句柄，仍失败则停止
        if (!emu.IsReady)
        {
            emu.RefreshWindowHandle();
            if (!emu.IsReady)
            {
                StopReason = "⛔ 模拟器已断开或窗口已关闭，已停止执行";
                _stop = true;
                _run.Set();
                Status?.Invoke(StopReason);
                return;
            }
        }

        switch (e.Type)
        {
            case EventType.MouseClick or EventType.MouseDown or EventType.MouseUp:
                if (e.Modifiers.Count > 0)
                    WarnOnce("mods", "模拟器模式不支持 Ctrl/Shift/Alt 修饰键，已忽略");
                var p = emu.Resolve(e);
                emu.Tap(p.X, p.Y);
                break;

            case EventType.MouseMove:
                break; // ADB 无独立移动语义，跳过

            case EventType.Wheel:
                var w = emu.Resolve(e);
                emu.Wheel(w.X, w.Y, e.Delta);
                break;

            case EventType.Key:
            {
                var code = AndroidKeys.FromName(KeyMap.NameOf(e.Vk));
                if (code > 0) emu.Key(code);
                else WarnOnce("key" + e.Vk, $"模拟器模式暂不支持按键 {KeyMap.NameOf(e.Vk)}，已跳过");
                break;
            }

            case EventType.Hotkey:
            {
                var keys = e.Combo.Where(k => !KeyMap.IsModifier(k)).ToList();
                if (keys.Count == 1)
                {
                    var code = AndroidKeys.FromName(KeyMap.NameOf(keys[0]));
                    if (code > 0) emu.Key(code);
                    else WarnOnce("combo" + keys[0], $"模拟器模式暂不支持按键 {KeyMap.NameOf(keys[0])}，已跳过");
                }
                else
                {
                    WarnOnce("combo", "模拟器模式暂不支持多键组合，已跳过");
                }
                break;
            }

            case EventType.Wait:
                break;
        }
    }

    private static void Execute(MacroEvent e)
    {
        switch (e.Type)
        {
            case EventType.MouseClick:
                ModsDown(e.Modifiers);
                Win32.SetCursorPos(e.X, e.Y);
                Thread.Sleep(10);
                Simulator.Button(e.Button, true);
                Thread.Sleep(15);
                Simulator.Button(e.Button, false);
                ModsUp(e.Modifiers);
                break;

            case EventType.MouseDown:
                ModsDown(e.Modifiers);
                Win32.SetCursorPos(e.X, e.Y);
                Thread.Sleep(10);
                Simulator.Button(e.Button, true);
                break;

            case EventType.MouseUp:
                Win32.SetCursorPos(e.X, e.Y);
                Thread.Sleep(10);
                Simulator.Button(e.Button, false);
                ModsUp(e.Modifiers);
                break;

            case EventType.MouseMove:
                Win32.SetCursorPos(e.X, e.Y);
                break;

            case EventType.Wheel:
                ModsDown(e.Modifiers);
                Win32.SetCursorPos(e.X, e.Y);
                Thread.Sleep(5);
                Simulator.Wheel(e.Delta);
                Thread.Sleep(5);
                ModsUp(e.Modifiers);
                break;

            case EventType.Key:
                Simulator.Key(e.Vk, true);
                Thread.Sleep(12);
                Simulator.Key(e.Vk, false);
                break;

            case EventType.Hotkey:
                for (int i = 0; i < e.Combo.Count; i++)
                {
                    Simulator.Key(e.Combo[i], true);
                    Thread.Sleep(8);
                }
                Thread.Sleep(15);
                for (int i = e.Combo.Count - 1; i >= 0; i--)
                {
                    Simulator.Key(e.Combo[i], false);
                    Thread.Sleep(8);
                }
                break;

            case EventType.Wait:
                break;
        }
    }

    private static void ModsDown(List<uint> mods)
    {
        foreach (var m in mods) { Simulator.Key(m, true); Thread.Sleep(5); }
    }

    private static void ModsUp(List<uint> mods)
    {
        for (int i = mods.Count - 1; i >= 0; i--) { Simulator.Key(mods[i], false); Thread.Sleep(5); }
    }
}
