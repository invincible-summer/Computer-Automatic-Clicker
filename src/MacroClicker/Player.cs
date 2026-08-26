using System.Diagnostics;

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
}

/// <summary>回放引擎：后台线程按 delta time 回放事件序列，支持循环、倍速、暂停与急停。</summary>
internal sealed class Player
{
    private volatile bool _stop;
    private bool _failSafeReported;
    private readonly ManualResetEventSlim _run = new(true);
    private Thread? _thread;
    private DateTime _lastStatusUtc = DateTime.MinValue;

    public bool IsBusy { get; private set; }
    public bool IsPaused { get; private set; }

    /// <summary>状态文本（后台线程回调）。</summary>
    public event Action<string>? Status;

    /// <summary>触发了左上角急停（后台线程回调）。</summary>
    public event Action? AbortedByFailSafe;

    /// <summary>回放结束：true = 正常完成，false = 被停止/急停。</summary>
    public event Action<bool>? Finished;

    public void Start(List<MacroEvent> events, PlaySettings settings)
    {
        if (IsBusy) return;
        _stop = false;
        _failSafeReported = false;
        IsPaused = false;
        _run.Set();
        _lastStatusUtc = DateTime.MinValue;
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
                    Execute(events[i]);
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
